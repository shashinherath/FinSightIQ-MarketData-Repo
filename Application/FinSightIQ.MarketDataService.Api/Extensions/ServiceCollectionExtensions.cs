using System.Net;
using FinSightIQ.MarketDataService.Api.Health;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Services;
using FinSightIQ.MarketDataService.Infrastructure.Caching;
using FinSightIQ.MarketDataService.Infrastructure.ExternalApis;
using FinSightIQ.MarketDataService.Infrastructure.Messaging;
using FinSightIQ.MarketDataService.Infrastructure.Persistence;
using FinSightIQ.MarketDataService.Infrastructure.Repositories;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;

namespace FinSightIQ.MarketDataService.Api.Extensions;

/// <summary>
/// Registers application, infrastructure, resilience, caching, messaging, and health check services.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMarketDataServiceDependencies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        DapperConfiguration.Configure();

        services.Configure<MarketDataOptions>(configuration.GetSection(MarketDataOptions.SectionName));
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));

        // Persistence & Repositories
        services.AddScoped<IDapperWrapper, DapperWrapper>();
        services.AddScoped<ISymbolRepository, SymbolRepository>();
        services.AddScoped<IStockPriceRepository, StockPriceRepository>();
        services.AddScoped<IProviderHealthRepository, ProviderHealthRepository>();

        // Redis Distributed Cache
        string? redisConnection = configuration.GetConnectionString("Redis")
            ?? configuration["Redis:ConnectionString"];

        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.AddScoped<IMarketDataCache, RedisMarketDataCache>();

        // RabbitMQ Event Publisher
        services.AddSingleton<IPriceEventPublisher, RabbitMqPriceEventPublisher>();

        // Market Data Providers with Microsoft.Extensions.Http.Resilience
        MarketDataOptions marketDataOptions = configuration
            .GetSection(MarketDataOptions.SectionName)
            .Get<MarketDataOptions>() ?? new MarketDataOptions();

        services.AddHttpClient<AlphaVantageProvider>((HttpClient client) =>
        {
            string baseUrl = string.IsNullOrWhiteSpace(marketDataOptions.AlphaVantage.BaseUrl)
                ? "https://www.alphavantage.co/"
                : marketDataOptions.AlphaVantage.BaseUrl;

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, marketDataOptions.AlphaVantage.TimeoutSeconds));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FinSightIQ-MarketDataService/1.0");
        })
        .AddStandardResilienceHandler(ConfigureProviderResilience);

        services.AddHttpClient<YahooFinanceProvider>((HttpClient client) =>
        {
            string baseUrl = string.IsNullOrWhiteSpace(marketDataOptions.YahooFinance.BaseUrl)
                ? "https://query1.finance.yahoo.com/"
                : marketDataOptions.YahooFinance.BaseUrl;

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, marketDataOptions.YahooFinance.TimeoutSeconds));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FinSightIQ-MarketDataService/1.0");
        })
        .AddStandardResilienceHandler(ConfigureProviderResilience);

        services.AddScoped<IMarketDataProvider>(sp => sp.GetRequiredService<AlphaVantageProvider>());
        services.AddScoped<IMarketDataProvider>(sp => sp.GetRequiredService<YahooFinanceProvider>());
        services.AddScoped<IMarketDataProviderOrchestrator, MarketDataProviderOrchestrator>();

        // Domain Services & Background Polling
        services.AddSingleton<IMarketHoursGuard, NyseMarketHoursGuard>();
        services.AddScoped<IMarketDataService, Core.Services.MarketDataService>();
        services.AddHostedService<PricePollingService>();

        // Health Checks
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>(
                "postgresql",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "ready" })
            .AddCheck<RedisHealthCheck>(
                "redis",
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "ready" });

        return services;
    }

    private static void ConfigureProviderResilience(HttpStandardResilienceOptions options)
    {
        // Retry 3 times with exponential backoff and jitter
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is HttpRequestException or TimeoutException ||
            args.Outcome.Result?.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout);

        // Circuit breaker opens after 5 failures (50% ratio, minimum throughput 5) for 30s break duration
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
    }
}

