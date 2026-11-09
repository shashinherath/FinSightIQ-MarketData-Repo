using FinSightIQ.BuildingBlocks.Middleware;
using FinSightIQ.BuildingBlocks.Results;
using Microsoft.AspNetCore.Mvc;

namespace FinSightIQ.MarketDataService.Api.Extensions;

/// <summary>
/// Maps domain <see cref="Error"/> instances into RFC 7807 Problem Details responses.
/// </summary>
public static class ProblemDetailsExtensions
{
    public static IActionResult ToProblemResult(
        this ControllerBase controller,
        Error error,
        IDictionary<string, string[]>? validationErrors = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(error);

        int statusCode = error.Code switch
        {
            "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
            "MALFORMED_REQUEST" => StatusCodes.Status400BadRequest,
            "UNAUTHENTICATED" => StatusCodes.Status401Unauthorized,
            "TOKEN_EXPIRED" => StatusCodes.Status401Unauthorized,
            "FORBIDDEN" => StatusCodes.Status403Forbidden,
            "SYMBOL_NOT_FOUND" => StatusCodes.Status404NotFound,
            "RESOURCE_NOT_FOUND" => StatusCodes.Status404NotFound,
            "RATE_LIMIT_EXCEEDED" => StatusCodes.Status429TooManyRequests,
            "UPSTREAM_RATE_LIMITED" => StatusCodes.Status503ServiceUnavailable,
            "UPSTREAM_UNAVAILABLE" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };

        string type = error.Code switch
        {
            "VALIDATION_FAILED" => "https://finsightiq.io/errors/validation-failed",
            "MALFORMED_REQUEST" => "https://finsightiq.io/errors/malformed-request",
            "UNAUTHENTICATED" => "https://finsightiq.io/errors/unauthenticated",
            "TOKEN_EXPIRED" => "https://finsightiq.io/errors/token-expired",
            "FORBIDDEN" => "https://finsightiq.io/errors/forbidden",
            "SYMBOL_NOT_FOUND" => "https://finsightiq.io/errors/symbol-not-found",
            "RESOURCE_NOT_FOUND" => "https://finsightiq.io/errors/resource-not-found",
            "RATE_LIMIT_EXCEEDED" => "https://finsightiq.io/errors/rate-limit-exceeded",
            "UPSTREAM_RATE_LIMITED" => "https://finsightiq.io/errors/upstream-unavailable",
            "UPSTREAM_UNAVAILABLE" => "https://finsightiq.io/errors/upstream-unavailable",
            _ => "https://finsightiq.io/errors/internal-error"
        };

        string title = error.Code switch
        {
            "VALIDATION_FAILED" => "Validation failed",
            "MALFORMED_REQUEST" => "Malformed request",
            "UNAUTHENTICATED" => "Authentication failed",
            "TOKEN_EXPIRED" => "Access token expired",
            "FORBIDDEN" => "Access forbidden",
            "SYMBOL_NOT_FOUND" => "Symbol not found",
            "RESOURCE_NOT_FOUND" => "Resource not found",
            "RATE_LIMIT_EXCEEDED" => "Rate limit exceeded",
            "UPSTREAM_RATE_LIMITED" => "Upstream provider unavailable",
            "UPSTREAM_UNAVAILABLE" => "Upstream provider unavailable",
            _ => "Internal server error"
        };

        string traceId = controller.HttpContext.Items[CorrelationId.HeaderName]?.ToString()
            ?? controller.HttpContext.TraceIdentifier;

        object problem = new
        {
            type,
            title,
            status = statusCode,
            code = error.Code,
            detail = error.Message,
            instance = controller.HttpContext.Request.Path.Value,
            traceId,
            errors = validationErrors
        };

        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json; charset=utf-8" }
        };
    }
}

