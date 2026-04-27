namespace HiveOps.Sandpit.Ui.Middleware;

public sealed class SandpitApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    private readonly string _expectedKey = config.GetValue<string>("SandpitApiKey") ?? "dev-sandbox";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith("/api/sandpit", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Only protect state-changing endpoints
        var method = context.Request.Method;
        if (method is not "POST" and not "PUT" and not "DELETE" and not "PATCH")
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers["X-Sandpit-Key"].FirstOrDefault();
        if (header != _expectedKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Missing or invalid X-Sandpit-Key header",
                expected = _expectedKey,
                hint = "Add header: X-Sandpit-Key: dev-sandbox"
            });
            return;
        }

        await next(context);
    }
}
