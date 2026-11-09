namespace FinSightIQ.MarketDataService.Infrastructure.ExternalApis;

/// <summary>
/// Configuration options for market data ingestion, caching, and upstream providers.
/// </summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    public bool PollingEnabled { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 60;
    public bool BypassMarketHoursGuard { get; set; }
    public int QuoteCacheTtlSeconds { get; set; } = 60;
    public int SymbolCacheTtlHours { get; set; } = 24;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public int CircuitBreakerBreakSeconds { get; set; } = 30;
    public AlphaVantageOptions AlphaVantage { get; set; } = new();
    public YahooFinanceOptions YahooFinance { get; set; } = new();
}

/// <summary>
/// Configuration options for the Alpha Vantage primary market data provider.
/// </summary>
public sealed class AlphaVantageOptions
{
    public string BaseUrl { get; set; } = "https://www.alphavantage.co/";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Configuration options for the Yahoo Finance secondary/fallback market data provider.
/// </summary>
public sealed class YahooFinanceOptions
{
    public string BaseUrl { get; set; } = "https://query1.finance.yahoo.com/";
    public int TimeoutSeconds { get; set; } = 10;
}

