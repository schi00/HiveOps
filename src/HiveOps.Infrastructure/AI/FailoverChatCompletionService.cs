using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace HiveOps.Infrastructure.AI;

/// <summary>Thrown fast while the provider-failover circuit is open (total LLM outage presumed).</summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string message) : base(message) { }
}

/// <summary>
/// IChatCompletionService that tries an ordered list of OpenAI-compatible providers
/// (OpenRouter, Groq, Gemini, ...) and falls through to the next one when the current
/// provider rejects the request (auth, rate limit, model unavailable, network).
/// Cancellation is never treated as provider failure.
/// </summary>
public sealed class FailoverChatCompletionService : IChatCompletionService
{
    // Process-wide circuit: shared across Kernel instances (a Kernel is built
    // per operation, so instance state would be useless). While open, calls
    // fail fast instead of walking the whole provider chain on every request.
    private static readonly TimeSpan CircuitWindow = TimeSpan.FromSeconds(45);
    private static DateTimeOffset _circuitOpenUntilUtc = DateTimeOffset.MinValue;

    /// <summary>True while every provider is presumed down. The retry worker keeps probing.</summary>
    public static bool CircuitOpen => DateTimeOffset.UtcNow < _circuitOpenUntilUtc;

    /// <summary>Test hook: resets the process-wide circuit between test cases.</summary>
    internal static void ResetCircuit() => _circuitOpenUntilUtc = DateTimeOffset.MinValue;

    /// <summary>Raised (via <see cref="LlmUnavailableException"/>) when the circuit is open.</summary>
    private static void ThrowIfCircuitOpen()
    {
        if (CircuitOpen)
            throw new LlmUnavailableException(
                $"All LLM providers are presumed down; circuit open until {_circuitOpenUntilUtc:u}.");
    }

    private readonly IReadOnlyList<IChatCompletionService> _providers;
    private readonly IReadOnlyList<string> _names;
    private readonly ILogger<FailoverChatCompletionService> _logger;

    public FailoverChatCompletionService(
        IEnumerable<(string Name, IChatCompletionService Service)> providers,
        ILogger<FailoverChatCompletionService> logger)
    {
        var list = providers.ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one chat provider is required.", nameof(providers));

        _providers = list.Select(p => p.Service).ToList();
        _names = list.Select(p => p.Name).ToList();
        _logger = logger;
    }

    public IReadOnlyDictionary<string, object?> Attributes => _providers[0].Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCircuitOpen();

        Exception? lastError = null;
        for (var i = 0; i < _providers.Count; i++)
        {
            try
            {
                var result = await _providers[i]
                    .GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken)
                    .ConfigureAwait(false);
                _circuitOpenUntilUtc = DateTimeOffset.MinValue;
                if (i > 0)
                    _logger.LogInformation("LLM failover: provider '{Provider}' answered after {Failed} failed attempt(s).", _names[i], i);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "LLM provider '{Provider}' failed; trying next in chain.", _names[i]);
            }
        }

        _circuitOpenUntilUtc = DateTimeOffset.UtcNow + CircuitWindow;
        _logger.LogError(
            "All {Count} LLM providers failed; circuit open for {Seconds}s.",
            _providers.Count, CircuitWindow.TotalSeconds);
        throw lastError!;
    }

    /// <summary>
    /// Streaming failover applies only until the first chunk is emitted by a provider;
    /// mid-stream failures propagate to the caller.
    /// </summary>
    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfCircuitOpen();

        Exception? lastError = null;
        for (var i = 0; i < _providers.Count; i++)
        {
            var stream = _providers[i].GetStreamingChatMessageContentsAsync(
                chatHistory, executionSettings, kernel, cancellationToken);
            await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);

            bool started;
            try
            {
                started = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "LLM provider '{Provider}' failed before first streamed chunk; trying next.", _names[i]);
                continue;
            }

            if (!started)
                yield break;

            if (i > 0)
                _logger.LogInformation("LLM failover: provider '{Provider}' streaming after {Failed} failed attempt(s).", _names[i], i);

            yield return enumerator.Current;
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                yield return enumerator.Current;
            _circuitOpenUntilUtc = DateTimeOffset.MinValue;
            yield break;
        }

        _circuitOpenUntilUtc = DateTimeOffset.UtcNow + CircuitWindow;
        throw lastError!;
    }

    /// <summary>Builds one OpenAI-compatible chat service per configured provider.</summary>
    public static FailoverChatCompletionService Create(
        IReadOnlyList<LlmProviderOptions> providers,
        string? modelIdOverride,
        ILogger<FailoverChatCompletionService> logger)
    {
        var services = providers
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.ApiKey) && !string.IsNullOrWhiteSpace(p.Endpoint))
            .Select(p => (
                string.IsNullOrWhiteSpace(p.Name) ? p.Endpoint : p.Name,
                (IChatCompletionService)new OpenAIChatCompletionService(
                    string.IsNullOrWhiteSpace(modelIdOverride) ? p.ModelId : modelIdOverride,
                    new OpenAIClient(
                        new ApiKeyCredential(p.ApiKey),
                        new OpenAIClientOptions { Endpoint = new Uri(p.Endpoint) }))))
            .ToList();

        return new FailoverChatCompletionService(services, logger);
    }
}
