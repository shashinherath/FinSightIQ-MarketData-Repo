using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;

namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Application service contract for market data queries, symbol search, and live quotes.
/// </summary>
public interface IMarketDataService
{
    Task<Result<PriceHistoryResponse>> GetPriceHistoryAsync(
        PriceHistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<Result<LiveQuoteDto>> GetLiveQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task<Result<SymbolSearchResponse>> SearchSymbolsAsync(
        string? query,
        CancellationToken cancellationToken = default);

    Task<Result<SymbolsListResponse>> GetSymbolsAsync(
        SymbolsFilterQuery query,
        CancellationToken cancellationToken = default);
}

