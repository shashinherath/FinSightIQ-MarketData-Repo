using FinSightIQ.MarketDataService.Core.DTOs;

namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Cache-aside abstraction for live stock quotes (60s TTL) and symbol metadata (24h TTL).
/// </summary>
public interface IMarketDataCache
{
    Task<LiveQuoteDto?> GetLiveQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task SetLiveQuoteAsync(
        LiveQuoteDto quote,
        CancellationToken cancellationToken = default);

    Task<SymbolDto?> GetSymbolAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task SetSymbolAsync(
        SymbolDto symbol,
        CancellationToken cancellationToken = default);
}

