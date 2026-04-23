using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace HiveOps.Api.Middleware;

/// <summary>
/// Rate limiting middleware for webhook and sensitive operation protection.
/// Prevents abuse by tracking requests per IP/user and enforcing limits.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private static readonly ConcurrentDictionary<string, RateLimitBucket> Buckets = new();

    // Stricter limits for dangerous operations
    private const int WebhookMaxRequestsPerMinute = 100;
    private const int SqlExecutionMaxRequestsPerMinute = 5; // Very strict for SQL execution
    private const int AdminApiMaxRequestsPerMinute = 30;

    public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Determine rate limit category and key
        var (isRateLimited, limitKey, maxRequests) = GetRateLimitConfig(context, path);

        if (!isRateLimited)
        {
            await _next(context);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var bucket = Buckets.AddOrUpdate(
            limitKey,
            _ => new RateLimitBucket { FirstRequestTime = now, RequestCount = 1, Category = GetCategory(path) },
            (_, existing) =>
            {
                var elapsed = (now - existing.FirstRequestTime).TotalSeconds;

                // Reset bucket if window has passed
                if (elapsed > 60)
                {
                    return new RateLimitBucket
                    {
                        FirstRequestTime = now,
                        RequestCount = 1,
                        Category = existing.Category
                    };
                }

                existing.RequestCount++;
                return existing;
            });

        var elapsedInWindow = (now - bucket.FirstRequestTime).TotalSeconds;

        if (elapsedInWindow <= 60 && bucket.RequestCount > maxRequests)
        {
            _logger.LogWarning(
                "Rate limit exceeded for {Category} by {Key}. Requests in window: {RequestCount}/{MaxRequests}",
                bucket.Category, limitKey, bucket.RequestCount, maxRequests);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";
            context.Response.Headers.Append("Retry-After", "60");
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Rate limit exceeded",
                category = bucket.Category,
                retryAfter = 60,
                maxRequests,
                windowSeconds = 60
            });
            return;
        }

        await _next(context);
    }

    private static (bool isRateLimited, string key, int maxRequests) GetRateLimitConfig(HttpContext context, string path)
    {
        // SQL execution endpoints - most restrictive
        if (path.Contains("/approve-db-fix") || path.Contains("/execute-sql") || path.Contains("/db-fix"))
        {
            var userKey = GetUserKey(context);
            return (true, $"sql:{userKey}", SqlExecutionMaxRequestsPerMinute);
        }

        // Admin API endpoints
        if (path.StartsWith("/api/admin/") || path.StartsWith("/api/support/incidents/"))
        {
            var userKey = GetUserKey(context);
            return (true, $"admin:{userKey}", AdminApiMaxRequestsPerMinute);
        }

        // Webhook endpoints
        if (path.StartsWith("/webhook") || path.StartsWith("/webhooks"))
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return (true, $"webhook:{ip}", WebhookMaxRequestsPerMinute);
        }

        return (false, string.Empty, 0);
    }

    private static string GetUserKey(HttpContext context)
    {
        // Use authenticated user identity if available, otherwise IP + User-Agent hash
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? context.User.Identity.Name
                ?? "unknown";
            return $"user:{userId}";
        }

        // For unauthenticated requests, use IP + User-Agent hash
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{ip}:{userAgent}")))[..16];
        return $"anon:{hash}";
    }

    private static string GetCategory(string path)
    {
        if (path.Contains("/approve-db-fix") || path.Contains("/execute-sql"))
            return "sql-execution";
        if (path.StartsWith("/api/admin/"))
            return "admin-api";
        if (path.StartsWith("/webhook"))
            return "webhook";
        return "general";
    }

    private sealed class RateLimitBucket
    {
        public DateTimeOffset FirstRequestTime { get; set; }
        public int RequestCount { get; set; }
        public string Category { get; set; } = "general";
    }
}
