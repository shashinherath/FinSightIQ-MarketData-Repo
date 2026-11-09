namespace FinSightIQ.MarketDataService.Core.DTOs;

/// <summary>
/// Represents a persisted row in the partitioned stock_prices table.
/// </summary>
public sealed record StockPriceEntity
{
    public long Id { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long Volume { get; init; }
    public string Interval { get; init; } = "1day";
    public string Source { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset IngestedAt { get; init; }
}

/// <summary>
/// Normalized OHLCV bar produced by market data providers and validated prior to persistence.
/// </summary>
public sealed record OhlcvBarDto(
    string Symbol,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    string Interval,
    string Source,
    DateTimeOffset RecordedAt);

/// <summary>
/// Public OHLCV data point returned in GET /api/v1/market/{symbol}/history.
/// </summary>
public sealed record HistoricalBarDto(
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    DateTimeOffset RecordedAt);

/// <summary>
/// Cursor pagination metadata following FinSightIQ API conventions.
/// </summary>
public sealed record PaginationMeta(
    string? NextCursor,
    bool HasMore,
    int Limit);

/// <summary>
/// Query parameters for GET /api/v1/market/{symbol}/history.
/// </summary>
public sealed record PriceHistoryQuery(
    string Symbol,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string Interval = "1day",
    int Limit = 50,
    string? Cursor = null);

/// <summary>
/// Response payload for GET /api/v1/market/{symbol}/history.
/// </summary>
public sealed record PriceHistoryResponse(
    string Symbol,
    string Interval,
    IReadOnlyList<HistoricalBarDto> Data,
    PaginationMeta Pagination);

/// <summary>
/// Response payload for GET /api/v1/market/{symbol}/live.
/// </summary>
public sealed record LiveQuoteDto(
    string Symbol,
    decimal Price,
    decimal Change,
    decimal ChangePercent,
    long Volume,
    DateTimeOffset AsOf,
    bool Cached);

