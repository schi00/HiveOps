using FluentValidation;
using HiveOps.Domain.Models;

namespace HiveOps.Api.Middleware;

/// <summary>
/// Validation middleware that validates IncomingMessage before processing.
/// </summary>
public sealed class ValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ValidationMiddleware> _logger;

    public ValidationMiddleware(RequestDelegate next, ILogger<ValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IValidator<IncomingMessage> validator)
    {
        var isWebhook = context.Request.Path.StartsWithSegments("/webhook", StringComparison.OrdinalIgnoreCase) ||
                       context.Request.Path.StartsWithSegments("/webhooks", StringComparison.OrdinalIgnoreCase);

        if (!isWebhook)
        {
            await _next(context);
            return;
        }

        // Store the original body stream
        var originalBodyStream = context.Request.Body;

        try
        {
            using var memoryStream = new MemoryStream();
            await context.Request.Body.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            // Check payload size (max 64KB for WhatsApp)
            if (memoryStream.Length > 65536)
            {
                _logger.LogWarning("Webhook payload size exceeds limit: {Size} bytes", memoryStream.Length);
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(new { error = "Payload too large" });
                return;
            }

            // Reset streams for next middleware
            memoryStream.Position = 0;
            context.Request.Body = memoryStream;

            await _next(context);
        }
        finally
        {
            context.Request.Body = originalBodyStream;
        }
    }
}
