using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace SaaSBot.Api.Services;

/// <summary>
/// Resilience policies using Polly for handling transient failures.
/// Provides retry and circuit breaker protection for HTTP and database operations.
/// </summary>
public sealed class ResilienceService
{
    private readonly IAsyncPolicy<HttpResponseMessage> _httpRetryPolicy;
    private readonly IAsyncPolicy<HttpResponseMessage> _httpCircuitBreakerPolicy;
    private readonly IAsyncPolicy<HttpResponseMessage> _httpCombinedPolicy;
    private readonly IAsyncPolicy _databaseRetryPolicy;
    private readonly ILogger<ResilienceService> _logger;

    public ResilienceService(ILogger<ResilienceService> logger)
    {
        _logger = logger;

        // ── HTTP Retry Policy ──────────────────────────────────────────────────
        // Retry 3 times with exponential backoff
        // Handles: transient HTTP errors (500, 503, 408, 429)
        _httpRetryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<OperationCanceledException>()
            .OrResult<HttpResponseMessage>(r =>
                r.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
                r.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                r.StatusCode == System.Net.HttpStatusCode.RequestTimeout ||
                r.StatusCode == (System.Net.HttpStatusCode)429) // Too Many Requests
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "HTTP request retry {RetryCount}/3 after {DelayMs}ms. Status: {StatusCode}",
                        retryCount,
                        timespan.TotalMilliseconds,
                        outcome.Result?.StatusCode ?? System.Net.HttpStatusCode.RequestTimeout);
                });

        // ── HTTP Circuit Breaker Policy ────────────────────────────────────────
        // Opens circuit if 5 consecutive failures occur
        // Waits 30 seconds before attempting half-open state (recovery)
        _httpCircuitBreakerPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<OperationCanceledException>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .CircuitBreakerAsync<HttpResponseMessage>(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (outcome, duration) =>
                {
                    _logger.LogError(
                        "Circuit breaker OPENED for {DurationSec}s due to {FailureCount} failures. " +
                        "External service appears unavailable.",
                        duration.TotalSeconds,
                        5);
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit breaker RESET. Resuming normal request flow.");
                },
                onHalfOpen: () =>
                {
                    _logger.LogInformation("Circuit breaker HALF-OPEN. Testing recovery...");
                });

        // ── Combined Policy (Retry + Circuit Breaker) ──────────────────────────
        // Retry first, then circuit breaker for resilience
        _httpCombinedPolicy = Policy.WrapAsync(_httpRetryPolicy, _httpCircuitBreakerPolicy);

        // ── Database Retry Policy ──────────────────────────────────────────────
        // Retry 3 times for transient DB failures (timeout, connection loss)
        _databaseRetryPolicy = Policy
            .Handle<TimeoutException>()
            .Or<InvalidOperationException>(ex => 
                ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 200),
                onRetry: (exception, timespan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "Database operation retry {RetryCount}/3 after {DelayMs}ms. Error: {Error}",
                        retryCount,
                        timespan.TotalMilliseconds,
                        exception.Message);
                });
    }

    /// <summary>
    /// Execute HTTP operation with automatic retry and circuit breaker protection.
    /// Used for external API calls (OpenRouter, WhatsApp, etc).
    /// </summary>
    public async Task<HttpResponseMessage> ExecuteHttpAsync(
        Func<Task<HttpResponseMessage>> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout for external APIs

            return await _httpCombinedPolicy.ExecuteAsync(async () =>
                await operation());
        }
        catch (BrokenCircuitException ex)
        {
            _logger.LogError(ex, 
                "Circuit breaker is OPEN for {OperationName}. Service is unavailable. " +
                "Requests will be rejected until circuit recovers.",
                operationName);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, 
                "HTTP operation {OperationName} timeout after 3 retries (10s limit)",
                operationName);
            throw;
        }
    }

    /// <summary>
    /// Execute database operation with automatic retry protection.
    /// Used for database queries that might be temporarily unavailable.
    /// </summary>
    public async Task ExecuteDbAsync(
        Func<Task> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30 second timeout for database ops

            await _databaseRetryPolicy.ExecuteAsync(async (ct) =>
                await operation(), cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Database operation {OperationName} failed after 3 retries. " +
                "Database may be unavailable.",
                operationName);
            throw;
        }
    }

    /// <summary>
    /// Execute database operation with automatic retry protection and return result.
    /// </summary>
    public async Task<T> ExecuteDbAsync<T>(
        Func<Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30 second timeout

            return await _databaseRetryPolicy.ExecuteAsync(async (ct) =>
                await operation(), cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Database operation {OperationName} failed after 3 retries.",
                operationName);
            throw;
        }
    }

    /// <summary>
    /// Get current circuit breaker state for monitoring.
    /// Useful for health checks and diagnostics.
    /// </summary>
    public CircuitState GetCircuitBreakerState()
    {
        if (_httpCircuitBreakerPolicy is CircuitBreakerPolicy<HttpResponseMessage> cbPolicy)
        {
            return cbPolicy.CircuitState;
        }
        return CircuitState.Closed;
    }
}
