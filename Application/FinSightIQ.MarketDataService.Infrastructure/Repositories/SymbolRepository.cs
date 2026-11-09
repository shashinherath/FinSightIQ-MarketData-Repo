using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;

namespace FinSightIQ.MarketDataService.Infrastructure.Repositories;

/// <summary>
/// Dapper repository implementation for the symbols table.
/// </summary>
public sealed class SymbolRepository : ISymbolRepository
{
    private readonly IDapperWrapper _dapper;

    public SymbolRepository(IDapperWrapper dapper)
    {
        ArgumentNullException.ThrowIfNull(dapper);
        _dapper = dapper;
    }

    public async Task<SymbolDto?> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        const string sql = "SELECT * FROM fn_GetSymbolByCode(@p_symbol);";
        return await _dapper.QuerySingleOrDefaultAsync<SymbolDto>(
            sql,
            new { p_symbol = symbol.Trim().ToUpperInvariant() },
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<SymbolDto>> SearchAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        int effectiveLimit = Math.Clamp(limit, 1, 20);
        const string sql = "SELECT * FROM fn_SearchSymbols(@p_query, @p_limit);";

        IEnumerable<SymbolDto> rows = await _dapper.QueryAsync<SymbolDto>(
            sql,
            new { p_query = query.Trim(), p_limit = effectiveLimit },
            cancellationToken: cancellationToken);

        return rows.ToList();
    }

    public async Task<IReadOnlyList<SymbolDto>> GetSymbolsAsync(
        string? exchange = null,
        string? sector = null,
        bool? isActive = null,
        string? cursorSymbol = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM fn_GetSymbols(@p_exchange, @p_sector, @p_is_active, @p_cursor_symbol, @p_limit);";

        IEnumerable<SymbolDto> rows = await _dapper.QueryAsync<SymbolDto>(
            sql,
            new
            {
                p_exchange = string.IsNullOrWhiteSpace(exchange) ? null : exchange.Trim().ToUpperInvariant(),
                p_sector = string.IsNullOrWhiteSpace(sector) ? null : sector.Trim(),
                p_is_active = isActive,
                p_cursor_symbol = string.IsNullOrWhiteSpace(cursorSymbol) ? null : cursorSymbol.Trim().ToUpperInvariant(),
                p_limit = limit
            },
            cancellationToken: cancellationToken);

        return rows.ToList();
    }

    public async Task<IReadOnlyList<SymbolDto>> GetActiveSymbolsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM fn_GetActiveSymbols();";

        IEnumerable<SymbolDto> rows = await _dapper.QueryAsync<SymbolDto>(
            sql,
            cancellationToken: cancellationToken);

        return rows.ToList();
    }

    public async Task<SymbolDto> UpsertAsync(SymbolDto symbol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        const string sql = @"
            SELECT * FROM fn_UpsertSymbol(
                @p_symbol,
                @p_name,
                @p_exchange,
                @p_sector,
                @p_currency,
                @p_is_active,
                @p_first_traded
            );";

        DateTime? firstTradedDate = symbol.FirstTraded.HasValue
            ? symbol.FirstTraded.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            : null;

        SymbolDto? persisted = await _dapper.QuerySingleOrDefaultAsync<SymbolDto>(
            sql,
            new
            {
                p_symbol = symbol.Symbol.Trim().ToUpperInvariant(),
                p_name = symbol.Name.Trim(),
                p_exchange = symbol.Exchange.Trim().ToUpperInvariant(),
                p_sector = symbol.Sector,
                p_currency = string.IsNullOrWhiteSpace(symbol.Currency) ? "USD" : symbol.Currency.Trim().ToUpperInvariant(),
                p_is_active = symbol.IsActive,
                p_first_traded = firstTradedDate
            },
            cancellationToken: cancellationToken);

        return persisted ?? throw new InvalidOperationException($"Failed to upsert symbol '{symbol.Symbol}'.");
    }
}

