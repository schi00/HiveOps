using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HiveOps.Application.Models;
using HiveOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0001, SKEXP0010

namespace HiveOps.Infrastructure.AI;

/// <summary>
/// Downloads media attachments referenced by inbound channel messages (Meta WhatsApp
/// media ids), extracts usable text (vision model for images, transcription for audio,
/// decoded text for text-like documents) and returns it so the orchestrator can merge
/// it into the incoming message. Only metadata/text is surfaced — binaries are never
/// persisted. All failures degrade to a safe placeholder, never throw.
/// </summary>
public sealed class MultimodalMediaProcessor
{
    private const int MaxMediaBytes = 10 * 1024 * 1024; // 10 MB cap
    private static readonly TimeSpan MediaTimeout = TimeSpan.FromSeconds(30);
    private const int MaxExtractedChars = 4_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly KernelFactory _kernelFactory;
    private readonly ILogger<MultimodalMediaProcessor> _logger;

    public MultimodalMediaProcessor(
        HttpClient httpClient,
        AppDbContext db,
        IConfiguration configuration,
        KernelFactory kernelFactory,
        ILogger<MultimodalMediaProcessor> logger)
    {
        _httpClient = httpClient;
        _db = db;
        _configuration = configuration;
        _kernelFactory = kernelFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns extracted text for the given media, or a user-safe placeholder.
    /// Never throws; returns <c>null</c> when nothing meaningful could be extracted
    /// and the caller should keep the original placeholder text.
    /// </summary>
    public async Task<string?> ProcessAsync(
        Guid tenantId,
        string mediaId,
        string? mimeType,
        string? caption,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
            return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MediaTimeout);
            var token = timeoutCts.Token;

            var accessToken = await ResolveWhatsAppTokenAsync(tenantId, token);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning("Media {MediaId} skipped: no WhatsApp access token for tenant {TenantId}.", mediaId, tenantId);
                return null;
            }

            var download = await DownloadMediaAsync(mediaId, accessToken, token);
            if (download.Bytes is null)
                return download.FailurePlaceholder;

            var bytes = download.Bytes;
            var effectiveMime = !string.IsNullOrWhiteSpace(mimeType) ? mimeType! : download.MimeType ?? "application/octet-stream";

            if (effectiveMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return await ExtractFromImageAsync(tenantId, bytes, effectiveMime, caption, token);

            if (effectiveMime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                return await ExtractFromAudioAsync(tenantId, bytes, effectiveMime, token);

            if (IsTextLikeDocument(effectiveMime))
                return DecodeTextDocument(bytes, effectiveMime);

            _logger.LogInformation(
                "Media {MediaId} ({Mime}, {Bytes} bytes) received; no extractor for this type.",
                mediaId, effectiveMime, bytes.Length);
            return $"[Adjunto recibido ({effectiveMime}) — no se pudo extraer contenido automáticamente]";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Media processing failed for media {MediaId} (tenant {TenantId}).", mediaId, tenantId);
            return null;
        }
    }

    // ── Meta Graph API: media url resolution + download ─────────────────────

    private sealed record MediaDownloadResult(byte[]? Bytes, string? MimeType, string? FailurePlaceholder);

    private async Task<MediaDownloadResult> DownloadMediaAsync(string mediaId, string accessToken, CancellationToken ct)
    {
        var graphBase = _configuration["WhatsApp:GraphApiBaseUrl"]?.TrimEnd('/')
            ?? "https://graph.facebook.com/v21.0";

        // Step 1: resolve the media URL + mime type.
        string? mediaUrl = null;
        string? mimeType = null;

        using (var metaRequest = new HttpRequestMessage(HttpMethod.Get, $"{graphBase}/{Uri.EscapeDataString(mediaId)}"))
        {
            metaRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var metaResponse = await _httpClient.SendAsync(metaRequest, ct);
            if (!metaResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Meta media metadata lookup failed for {MediaId}. Status={StatusCode}",
                    mediaId, (int)metaResponse.StatusCode);
                return new MediaDownloadResult(null, null, null);
            }

            var metaJson = await metaResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(metaJson);
            if (doc.RootElement.TryGetProperty("url", out var urlProp))
                mediaUrl = urlProp.GetString();
            if (doc.RootElement.TryGetProperty("mime_type", out var mimeProp))
                mimeType = mimeProp.GetString();
        }

        if (string.IsNullOrWhiteSpace(mediaUrl))
        {
            _logger.LogWarning("Meta media {MediaId} did not return a download url.", mediaId);
            return new MediaDownloadResult(null, null, null);
        }

        // Step 2: download the binary with a hard size cap.
        using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Meta media download failed for {MediaId}. Status={StatusCode}",
                mediaId, (int)response.StatusCode);
            return new MediaDownloadResult(null, null, null);
        }

        if (response.Content.Headers.ContentLength is > MaxMediaBytes)
        {
            _logger.LogWarning(
                "Meta media {MediaId} exceeds {MaxBytes} byte cap; download skipped.",
                mediaId, MaxMediaBytes);
            return new MediaDownloadResult(null, null, $"[Adjunto demasiado grande (más de {MaxMediaBytes / (1024 * 1024)} MB) — no se pudo procesar]");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxMediaBytes)
            {
                _logger.LogWarning("Meta media {MediaId} exceeded {MaxBytes} byte cap mid-stream.", mediaId, MaxMediaBytes);
                return new MediaDownloadResult(null, null, $"[Adjunto demasiado grande (más de {MaxMediaBytes / (1024 * 1024)} MB) — no se pudo procesar]");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return new MediaDownloadResult(buffer.ToArray(), mimeType ?? response.Content.Headers.ContentType?.MediaType, null);
    }

    // ── Per-type extractors ─────────────────────────────────────────────────

    private async Task<string?> ExtractFromImageAsync(Guid tenantId, byte[] imageBytes, string mimeType, string? caption, CancellationToken ct)
    {
        try
        {
            var kernel = _kernelFactory.CreateForTenant(tenantId);
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var history = new ChatHistory();
            history.AddSystemMessage(
                "Sos un asistente de soporte técnico. El usuario envió una imagen como evidencia de un problema. " +
                "Extraé todo el texto visible (errores, códigos, mensajes) y describí brevemente lo que muestra la imagen. " +
                "Respondé en español, de forma concisa.");
            var items = new ChatMessageContentItemCollection
            {
                new TextContent(string.IsNullOrWhiteSpace(caption)
                    ? "Analizá esta imagen adjunta."
                    : $"Analizá esta imagen adjunta. El usuario la acompañó con este texto: \"{caption}\""),
                new ImageContent(new ReadOnlyMemory<byte>(imageBytes), mimeType)
            };
            history.AddUserMessage(items);

            var reply = await chat.GetChatMessageContentAsync(history, cancellationToken: ct);
            var extracted = reply?.Content?.Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
                return Truncate(extracted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision extraction failed for tenant {TenantId}; degrading to placeholder.", tenantId);
        }

        return "[Imagen recibida — no se pudo analizar automáticamente. Describí brevemente lo que muestra o el error visible]";
    }

    private async Task<string?> ExtractFromAudioAsync(Guid tenantId, byte[] audioBytes, string mimeType, CancellationToken ct)
    {
        if (!_configuration.GetValue<bool>("Media:EnableAudioTranscription"))
            return "[Audio recibido — transcripción no habilitada]";

        var endpoint = _configuration["Media:TranscriptionEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("Media:EnableAudioTranscription=true but Media:TranscriptionEndpoint is not configured.");
            return "[Audio recibido — transcripción no configurada]";
        }

        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(audioBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            content.Add(fileContent, "file", $"audio{GuessExtension(mimeType)}");
            content.Add(new StringContent(_configuration["Media:TranscriptionModel"] ?? "whisper-1"), "model");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            var apiKey = _configuration["Media:TranscriptionApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Audio transcription endpoint failed for tenant {TenantId}. Status={StatusCode}",
                    tenantId, (int)response.StatusCode);
                return "[Audio recibido — la transcripción falló]";
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
                return Truncate(text.Trim());

            return "[Audio recibido — transcripción vacía]";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio transcription failed for tenant {TenantId}.", tenantId);
            return "[Audio recibido — la transcripción falló]";
        }
    }

    private static bool IsTextLikeDocument(string mimeType)
    {
        return mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/javascript", StringComparison.OrdinalIgnoreCase)
            || mimeType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mimeType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeTextDocument(byte[] bytes, string mimeType)
    {
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(text))
                return $"[Documento de texto vacío ({mimeType})]";
            return $"[Contenido del documento]\n{Truncate(text.Trim())}";
        }
        catch
        {
            return $"[Documento recibido ({mimeType}) — no se pudo decodificar como texto]";
        }
    }

    private static string GuessExtension(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "audio/ogg" => ".ogg",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/m4a" => ".m4a",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/webm" => ".webm",
        _ => ".bin"
    };

    // ── Tenant WhatsApp token resolution (mirrors MetaWhatsAppChannel) ──────

    private async Task<string?> ResolveWhatsAppTokenAsync(Guid tenantId, CancellationToken ct)
    {
        string? apiKey = null;
        try
        {
            var tenant = await _db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive, ct);

            if (tenant is not null)
                apiKey = ParseTenantSettings(tenant.ConfigJson).WhatsApp.ApiKey;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tenant {TenantId} WhatsApp settings for media download.", tenantId);
        }

        // Fallback: global API key from configuration (same precedence as MetaWhatsAppChannel).
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = _configuration["WhatsApp:ApiKey"];

        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    private static TenantAdminSettings ParseTenantSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TenantAdminSettings();

        try
        {
            return JsonSerializer.Deserialize<TenantAdminSettings>(json, JsonOptions) ?? new TenantAdminSettings();
        }
        catch (JsonException)
        {
            return new TenantAdminSettings();
        }
    }

    private static string Truncate(string text)
        => text.Length <= MaxExtractedChars ? text : text[..MaxExtractedChars];
}
