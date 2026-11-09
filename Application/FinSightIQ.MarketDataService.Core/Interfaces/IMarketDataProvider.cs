using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;

namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Abstraction for external market data providers (e.g., Alpha Vantage, Yahoo Finance).
/// </summary>
public interface IMarketDataProvider
{
    /// <summary>
    /// Canonical provider identifier (e.g., "AlphaVantage", "YahooFinance").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Fetches recent OHLCV bars for a symbol at the requested interval.
    /// </summary>
    Task<Result<IReadOnlyList<OhlcvBarDto>>> GetBarsAsync(
        string symbol,
        string interval = "1day",
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates primary and fallback market data providers with health tracking.
/// </summary>
public interface IMarketDataProviderOrchestrator
{
    Task<Result<IReadOnlyList<OhlcvBarDto>>> FetchBarsWithFallbackAsync(
        string symbol,
        string interval = "1day",
        CancellationToken cancellationToken = default);
}

