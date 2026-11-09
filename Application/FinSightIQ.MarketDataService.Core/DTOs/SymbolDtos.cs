namespace FinSightIQ.MarketDataService.Core.DTOs;

/// <summary>
/// Represents a tracked market equity symbol from the symbols master table.
/// </summary>
public sealed record SymbolDto
{
    public string Symbol { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Exchange { get; init; } = string.Empty;
    public string? Sector { get; init; }
    public string Currency { get; init; } = "USD";
    public bool IsActive { get; init; } = true;
    public DateOnly? FirstTraded { get; init; }
}

/// <summary>
/// Represents a single symbol search match returned by GET /api/v1/market/search.
/// </summary>
public sealed record SymbolSearchResultDto(
    string Symbol,
    string Name,
    string Exchange,
    string? Sector);

/// <summary>
/// Envelope for symbol search results returned by GET /api/v1/market/search.
/// </summary>
public sealed record SymbolSearchResponse(
    IReadOnlyList<SymbolSearchResultDto> Results);

/// <summary>
/// Query parameters for listing tracked symbols via GET /api/v1/market/symbols.
/// </summary>
public sealed record SymbolsFilterQuery(
    string? Exchange = null,
    string? Sector = null,
    bool? Active = null,
    int Limit = 50,
    string? Cursor = null);

/// <summary>
/// Paginated response for GET /api/v1/market/symbols.
/// </summary>
public sealed record SymbolsListResponse(
    IReadOnlyList<SymbolDto> Data,
    PaginationMeta Pagination);

