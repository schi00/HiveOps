using System.Net;
using System.Text.Json;

namespace SaaSBot.Api.Middleware;

/// <summary>
/// Global exception handler middleware.
/// Catches all exceptions and returns consistent error responses with proper logging.
/// </summary>
public sealed class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Request cancelled for {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
            await WriteErrorResponseAsync(context, "Request timeout", StatusCodes.Status408RequestTimeout);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error for {Path}: {Message}", context.Request.Path, ex.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteErrorResponseAsync(context, ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation for {Path}: {Message}", context.Request.Path, ex.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteErrorResponseAsync(context, ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled exception for {Method} {Path}. RequestId: {RequestId}",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteErrorResponseAsync(
                context,
                "An internal error occurred. Please contact support.",
                StatusCodes.Status500InternalServerError,
                context.TraceIdentifier);
        }
    }

    private static Task WriteErrorResponseAsync(
        HttpContext context,
        string message,
        int statusCode,
        string? traceId = null)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            error = new
            {
                message,
                status = statusCode,
                traceId = traceId ?? context.TraceIdentifier,
                timestamp = DateTimeOffset.UtcNow
            }
        };

        return context.Response.WriteAsJsonAsync(response);
    }
}
