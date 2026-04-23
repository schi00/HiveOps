using System.Collections.Concurrent;
using System.Net;

namespace HiveOps.Api.Middleware;

/// <summary>
/// Rate limiting middleware for webhook protection.
/// Prevents abuse by tracking requests per IP/tenant and enforcing limits.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private static readonly ConcurrentDictionary<string, RateLimitBucket> Buckets = new();

    public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isWebhook = context.Request.Path.StartsWithSegments("/webhook", StringComparison.OrdinalIgnoreCase) ||
                       context.Request.Path.StartsWithSegments("/webhooks", StringComparison.OrdinalIgnoreCase);

        if (!isWebhook)
        {
            await _next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTimeOffset.UtcNow;

        var bucket = Buckets.AddOrUpdate(
            remoteIp,
            _ => new RateLimitBucket { FirstRequestTime = now, RequestCount = 1 },
            (_, existing) =>
            {
                var elapsed = (now - existing.FirstRequestTime).TotalSeconds;
                
                if (elapsed > 60)
                {
                    return new RateLimitBucket { FirstRequestTime = now, RequestCount = 1 };
                }

                existing.RequestCount++;
                return existing;
            });

        const int maxRequestsPerMinute = 100;
        var elapsed2 = (now - bucket.FirstRequestTime).TotalSeconds;

        if (elapsed2 <= 60 && bucket.RequestCount > maxRequestsPerMinute)
        {
            _logger.LogWarning(
                "Rate limit exceeded for {RemoteIp}. Requests in window: {RequestCount}",
                remoteIp, bucket.RequestCount);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Rate limit exceeded",
                retryAfter = 60
            });
            return;
        }

        await _next(context);
    }

    private sealed class RateLimitBucket
    {
        public DateTimeOffset FirstRequestTime { get; set; }
        public int RequestCount { get; set; }
    }
}
