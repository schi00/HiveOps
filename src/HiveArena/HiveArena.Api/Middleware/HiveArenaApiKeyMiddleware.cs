using HiveArena.Api.Configuration;
using Microsoft.Extensions.Options;

namespace HiveArena.Api.Middleware;

public sealed class HiveArenaApiKeyMiddleware(RequestDelegate next, IOptions<HiveArenaHostOptions> options)
{
    private readonly HiveArenaHostOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/hivearena", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-HiveArena-Key", out var key) || key != _options.ApiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-HiveArena-Key" });
            return;
        }

        await next(context);
    }
}
