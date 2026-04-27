using System.Diagnostics;

namespace HiveOps.Sandpit.Ui.Middleware;

public sealed class SandpitTelemetryMiddleware(RequestDelegate next, ILogger<SandpitTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith("/api/sandpit", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "[SANDBOX-REQUEST] {Method} {Path} started at {Timestamp:O}",
            context.Request.Method,
            path,
            DateTimeOffset.UtcNow);

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[SANDBOX-ERROR] {Method} {Path} failed after {ElapsedMs}ms",
                context.Request.Method,
                path,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation(
                "[SANDBOX-RESPONSE] {Method} {Path} => {StatusCode} in {ElapsedMs}ms",
                context.Request.Method,
                path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
