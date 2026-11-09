using System.Reflection;
using System.Text.Json;
using FinSightIQ.BuildingBlocks.Middleware;
using FinSightIQ.MarketDataService.Api.Extensions;
using FinSightIQ.MarketDataService.Api.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 1. Structured Logging with Serilog
builder.Host.UseSerilog((HostBuilderContext context, LoggerConfiguration configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "finsightiq-market-data-service")
        .WriteTo.Console();
});

// 2. OpenTelemetry Distributed Tracing
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("finsightiq-market-data-service"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
    });

// 3. Application & Infrastructure Dependencies
builder.Services.AddMarketDataServiceDependencies(builder.Configuration);

// 4. Controllers & RFC 7807 Validation Problem Details
builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = (ActionContext context) =>
        {
            Dictionary<string, string[]> errors = context.ModelState
                .Where(kvp => kvp.Value is not null && kvp.Value.Errors.Count > 0)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value!.Errors
                        .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage)
                        .ToArray());

            string traceId = context.HttpContext.Items[CorrelationId.HeaderName]?.ToString()
                ?? context.HttpContext.TraceIdentifier;

            object problem = new
            {
                type = "https://finsightiq.io/errors/validation-failed",
                title = "Validation failed",
                status = StatusCodes.Status400BadRequest,
                code = "VALIDATION_FAILED",
                detail = "One or more request parameters failed validation.",
                instance = context.HttpContext.Request.Path.Value,
                traceId,
                errors
            };

            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json; charset=utf-8" }
            };
        };
    });

// 5. OpenAPI / Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "FinSightIQ Market Data Service API",
        Version = "v1",
        Description = "Ingests, validates, caches, and serves historical OHLCV bars, live stock quotes, and symbol search."
    });

    string xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

WebApplication app = builder.Build();

// 6. Middleware Pipeline
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<UserContextMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting();
app.UseHttpMetrics();

app.MapControllers();
app.MapMetrics();

// 7. Health Check Endpoints (/health, /health/live, /health/ready)
JsonSerializerOptions healthJsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = async (HttpContext context, HealthReport report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        string json = JsonSerializer.Serialize(new { status = report.Status.ToString() }, healthJsonOptions);
        await context.Response.WriteAsync(json);
    }
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    },
    ResponseWriter = WriteReadinessReportAsync
});

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    },
    ResponseWriter = WriteReadinessReportAsync
});

await app.RunAsync();

static async Task WriteReadinessReportAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json; charset=utf-8";

    Dictionary<string, object> entries = report.Entries.ToDictionary(
        kvp => kvp.Key,
        kvp => (object)new
        {
            status = kvp.Value.Status.ToString(),
            durationMs = Math.Round(kvp.Value.Duration.TotalMilliseconds, 2),
            description = kvp.Value.Description
        });

    object payload = new
    {
        status = report.Status.ToString(),
        totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
        entries
    };

    string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    await context.Response.WriteAsync(json);
}

/// <summary>
/// Entry point marker for WebApplicationFactory integration and contract tests.
/// </summary>
public partial class Program
{
}
