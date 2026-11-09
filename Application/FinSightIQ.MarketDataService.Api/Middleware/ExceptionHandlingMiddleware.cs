using System.Text.Json;
using FinSightIQ.BuildingBlocks.Exceptions;
using FinSightIQ.BuildingBlocks.Middleware;

namespace FinSightIQ.MarketDataService.Api.Middleware;

/// <summary>
/// Global exception handling middleware producing RFC 7807 Problem Details responses
/// while ensuring stack traces are never leaked to callers.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Request {Method} {Path} was canceled by the client.",
                context.Request.Method,
                context.Request.Path.Value);
        }
        catch (Exception ex)
        {
            LogException(context, ex);
            await WriteProblemDetailsAsync(context, ex);
        }
    }

    private void LogException(HttpContext context, Exception exception)
    {
        string traceId = context.Items[CorrelationId.HeaderName]?.ToString()
            ?? context.TraceIdentifier;

        LogLevel level = exception switch
        {
            DomainException => LogLevel.Warning,
            ArgumentException => LogLevel.Warning,
            KeyNotFoundException => LogLevel.Information,
            UnauthorizedAccessException => LogLevel.Warning,
            _ => LogLevel.Error
        };

        _logger.Log(
            level,
            exception,
            "Unhandled exception in {Method} {Path} (TraceId={TraceId}): {ExceptionType} - {Message}",
            context.Request.Method,
            context.Request.Path.Value,
            traceId,
            exception.GetType().Name,
            exception.Message);
    }

    private static async Task WriteProblemDetailsAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        (int statusCode, string code, string title, string detail, string type) = exception switch
        {
            DomainException dex => (
                StatusCodes.Status422UnprocessableEntity,
                dex.Code,
                "Domain rule violation",
                dex.Message,
                $"https://finsightiq.io/errors/{dex.Code.ToLowerInvariant().Replace('_', '-')}"),
            KeyNotFoundException knf => (
                StatusCodes.Status404NotFound,
                "SYMBOL_NOT_FOUND",
                "Symbol not found",
                knf.Message,
                "https://finsightiq.io/errors/symbol-not-found"),
            ArgumentException argEx => (
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                argEx.Message,
                "https://finsightiq.io/errors/validation-failed"),
            UnauthorizedAccessException => (
                StatusCodes.Status401Unauthorized,
                "UNAUTHENTICATED",
                "Authentication failed",
                "Access denied.",
                "https://finsightiq.io/errors/unauthenticated"),
            TimeoutException => (
                StatusCodes.Status503ServiceUnavailable,
                "UPSTREAM_UNAVAILABLE",
                "Upstream provider unavailable",
                "The market data operation timed out.",
                "https://finsightiq.io/errors/upstream-unavailable"),
            _ => (
                StatusCodes.Status500InternalServerError,
                "INTERNAL_ERROR",
                "Internal server error",
                "An unexpected internal server error occurred.",
                "https://finsightiq.io/errors/internal-error")
        };

        string traceId = context.Items[CorrelationId.HeaderName]?.ToString()
            ?? context.TraceIdentifier;

        object problem = new
        {
            type,
            title,
            status = statusCode,
            code,
            detail,
            instance = context.Request.Path.Value,
            traceId,
            errors = (object?)null
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json; charset=utf-8";

        string json = JsonSerializer.Serialize(problem, JsonOptions);
        await context.Response.WriteAsync(json);
    }
}

