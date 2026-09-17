using System.Text.Json;

namespace Vouch.Api.Middleware;

/// <summary>
/// Step 10: Global exception handler middleware.
/// Maps domain exception types to consistent HTTP status codes and a uniform
/// error envelope: { "error": "...", "traceId": "..." }.
/// Eliminates the need for per-endpoint try/catch blocks.
/// </summary>
public class GlobalExceptionHandlerMiddleware
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

    public GlobalExceptionHandlerMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionHandlerMiddleware> logger)
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
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var (statusCode, message) = ex switch
        {
            // 400 — caller sent bad data
            ArgumentException       => (StatusCodes.Status400BadRequest,  ex.Message),
            FormatException         => (StatusCodes.Status400BadRequest,  "Invalid request format."),

            // 401 — authentication failed
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, ex.Message),

            // 403 — authenticated but not permitted (re-throw as 403 not 401)
            InvalidOperationException { Message: var m } when
                m.Contains("suspended", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("locked",    StringComparison.OrdinalIgnoreCase)
                => (StatusCodes.Status403Forbidden, m),

            // 404 — resource not found
            KeyNotFoundException    => (StatusCodes.Status404NotFound,   ex.Message),

            // 409 — business rule conflict (duplicate, state violation, etc.)
            InvalidOperationException => (StatusCodes.Status409Conflict, ex.Message),

            // 500 — everything else is unexpected
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred. Please try again later.")
        };

        // Log 5xx at Error level, 4xx at Warning to avoid log spam
        if (statusCode >= 500)
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        else
            _logger.LogWarning(ex, "Handled exception [{Status}] on {Method} {Path}", statusCode, context.Request.Method, context.Request.Path);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var traceId = context.TraceIdentifier;
        var body = JsonSerializer.Serialize(new { error = message, traceId }, _jsonOptions);
        await context.Response.WriteAsync(body);
    }
}
