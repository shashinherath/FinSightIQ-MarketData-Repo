using FinSightIQ.MarketDataService.Core.DTOs;

namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Repository contract for querying and persisting OHLCV bars in the partitioned stock_prices table.
/// </summary>
public interface IStockPriceRepository
{
    Task<IReadOnlyList<StockPriceEntity>> GetHistoricalPricesAsync(
        string symbol,
        string interval,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        DateTimeOffset? cursorRecordedAt = null,
        long? cursorId = null,
        int limit = 51,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockPriceEntity>> GetLatestPricesAsync(
        string symbol,
        string? interval = null,
        int count = 2,
        CancellationToken cancellationToken = default);

    Task<StockPriceEntity> UpsertAsync(
        OhlcvBarDto bar,
        CancellationToken cancellationToken = default);

    Task<int> BulkUpsertAsync(
        IReadOnlyList<OhlcvBarDto> bars,
        CancellationToken cancellationToken = default);

    Task EnsurePartitionExistsAsync(
        int year,
        int month,
        CancellationToken cancellationToken = default);
}

