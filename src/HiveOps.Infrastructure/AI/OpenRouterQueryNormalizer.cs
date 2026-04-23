using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HiveOps.Domain.Interfaces;
using HiveOps.Domain.Models;

namespace HiveOps.Infrastructure.AI;

public sealed class OpenRouterQueryNormalizer : IQueryNormalizer
{
    private const string PromptVersion = "v1-multirubro";
    private const string SystemPrompt = """
Eres un normalizador semántico para búsquedas de catálogo en múltiples rubros.
Responde exclusivamente con JSON válido (sin markdown ni texto adicional) con este contrato:
{
  "search_term": "string",
  "entities": { "key": "value" },
  "is_search_query": true|false,
  "intent": "search|question|chitchat|action",
  "confidence": 0.0-1.0,
  "language": "es|en|pt|other",
  "brand": "string|null",
  "category": "string|null"
}
Reglas:
1) Si no es intención de búsqueda de catálogo, usa is_search_query=false y search_term vacío.
2) Corrige typos sin inventar marcas/categorías fuera del contexto recibido.
3) Prioriza precisión y consistencia sobre creatividad.
4) entities debe incluir únicamente filtros útiles (color, talla, tamaño, presentación, etc).
""";

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly QueryNormalizerOptions _options;
    private readonly SemanticKernelOptions _semanticKernelOptions;
    private readonly ILogger<OpenRouterQueryNormalizer> _logger;

    public OpenRouterQueryNormalizer(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<QueryNormalizerOptions> options,
        IOptions<SemanticKernelOptions> semanticKernelOptions,
        ILogger<OpenRouterQueryNormalizer> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _semanticKernelOptions = semanticKernelOptions.Value;
        _logger = logger;
    }

    public async Task<QueryNormalizationResult> NormalizeAsync(string rawMessage, string tenantContext, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return QueryNormalizationResult.Empty;

        if (!_options.Enabled)
            return QueryNormalizationResult.Empty;

        var modelId = ResolveModelId();
        var apiKey = _semanticKernelOptions.OpenRouter.ApiKey?.Trim();
        var endpoint = _semanticKernelOptions.OpenRouter.Endpoint?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(modelId))
        {
            _logger.LogWarning("OpenRouter query normalizer is enabled but configuration is incomplete.");
            return QueryNormalizationResult.Empty;
        }

        tenantContext = string.IsNullOrWhiteSpace(tenantContext) ? "tenant_industry: general" : tenantContext.Trim();
        var cacheKey = BuildCacheKey(rawMessage, tenantContext, modelId);
        if (_cache.TryGetValue(cacheKey, out QueryNormalizationResult? cached) && cached is not null)
            return cached;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 2, 20)));

        try
        {
            var payload = BuildPayload(modelId, rawMessage, tenantContext);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Add("X-Title", _semanticKernelOptions.OpenRouter.AppName);
            if (!string.IsNullOrWhiteSpace(_semanticKernelOptions.OpenRouter.Referer))
                request.Headers.Add("HTTP-Referer", _semanticKernelOptions.OpenRouter.Referer);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenRouter query normalizer call failed with status {StatusCode}", response.StatusCode);
                return QueryNormalizationResult.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            var parsed = ParseChatCompletionResponse(document.RootElement);
            _cache.Set(cacheKey, parsed, _options.CacheTtl);
            return parsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("OpenRouter query normalization timed out.");
            return QueryNormalizationResult.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenRouter query normalization failed. Falling back to raw query.");
            return QueryNormalizationResult.Empty;
        }
    }

    private object BuildPayload(string modelId, string rawMessage, string tenantContext)
    {
        var userPrompt = BuildUserPrompt(rawMessage, tenantContext);
        return new
        {
            model = modelId,
            temperature = _options.Temperature,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userPrompt }
            }
        };
    }

    private static string BuildUserPrompt(string rawMessage, string tenantContext)
    {
        return """
Contexto del tenant:
""" + tenantContext + """

Mensaje del usuario:
""" + rawMessage + """

Ejemplos:
Input: "tenes ibuprfeno 600?"
Output: {"search_term":"ibuprofeno 600","entities":{"dosage":"600"},"is_search_query":true,"intent":"search","confidence":0.94,"language":"es","brand":null,"category":"farmacia"}
Input: "hola como estas"
Output: {"search_term":"","entities":{},"is_search_query":false,"intent":"chitchat","confidence":0.99,"language":"es","brand":null,"category":null}
Input: "busco tornillos phillips 5mm"
Output: {"search_term":"tornillos phillips 5mm","entities":{"size":"5mm"},"is_search_query":true,"intent":"search","confidence":0.95,"language":"es","brand":null,"category":"ferreteria"}
Input: "do you have gluten free pasta?"
Output: {"search_term":"gluten free pasta","entities":{"diet":"gluten free"},"is_search_query":true,"intent":"search","confidence":0.93,"language":"en","brand":null,"category":"alimentos"}
""";
    }

    private string ResolveModelId()
    {
        if (!string.IsNullOrWhiteSpace(_options.ModelId))
            return _options.ModelId.Trim();

        if (_semanticKernelOptions.Profiles.TryGetValue(_options.ProfileName, out var namedProfile) && namedProfile.Enabled)
            return namedProfile.ModelId;

        return _semanticKernelOptions.GetActiveProfile().ModelId;
    }

    private static QueryNormalizationResult ParseChatCompletionResponse(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return QueryNormalizationResult.Empty;
        }

        var content = choices[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            return QueryNormalizationResult.Empty;

        try
        {
            var normalized = NormalizeJsonText(content);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<QueryNormalizationResult>(normalized, options) ?? QueryNormalizationResult.Empty;
            result.SearchTerm = result.SearchTerm?.Trim() ?? string.Empty;
            result.Entities ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result.Intent = string.IsNullOrWhiteSpace(result.Intent) ? "search" : result.Intent.Trim().ToLowerInvariant();
            result.Language = string.IsNullOrWhiteSpace(result.Language) ? "es" : result.Language.Trim().ToLowerInvariant();
            result.Confidence = Math.Clamp(result.Confidence, 0, 1);
            result.IsSearchQuery = result.IsSearchQuery || !string.IsNullOrWhiteSpace(result.SearchTerm) || result.Entities.Count > 0;
            return result;
        }
        catch
        {
            return QueryNormalizationResult.Empty;
        }
    }

    private static string NormalizeJsonText(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal) && cleaned.EndsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = cleaned.IndexOf('\n');
            if (firstNewLine > 0)
            {
                cleaned = cleaned[(firstNewLine + 1)..].Trim();
                if (cleaned.EndsWith("```", StringComparison.Ordinal))
                    cleaned = cleaned[..^3].Trim();
            }
        }

        return cleaned;
    }

    private static string BuildCacheKey(string rawMessage, string tenantContext, string modelId)
    {
        var input = $"{PromptVersion}||{modelId}||{rawMessage.Trim()}||{tenantContext.Trim()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "query-normalizer:" + Convert.ToHexString(bytes);
    }
}
