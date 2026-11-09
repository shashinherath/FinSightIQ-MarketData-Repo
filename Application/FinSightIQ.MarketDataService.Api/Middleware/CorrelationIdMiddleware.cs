using FinSightIQ.BuildingBlocks.Middleware;
using Serilog.Context;

namespace FinSightIQ.MarketDataService.Api.Middleware;

/// <summary>
/// Propagates or generates the X-Correlation-Id header and enriches the Serilog log context.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? rawCorrelation = context.Request.Headers[CorrelationId.HeaderName].FirstOrDefault();

        CorrelationId correlationId = CorrelationId.TryParse(rawCorrelation, out CorrelationId? parsed) && parsed is not null
            ? parsed
            : CorrelationId.NewId();

        context.Items[CorrelationId.HeaderName] = correlationId.Value;
        context.Response.Headers[CorrelationId.HeaderName] = correlationId.Value;

        using (LogContext.PushProperty("CorrelationId", correlationId.Value))
        {
            await _next(context);
        }
    }
}

