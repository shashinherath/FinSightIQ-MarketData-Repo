using FinSightIQ.MarketDataService.Core.DTOs;

namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Repository contract for querying and managing market symbols in marketdb.
/// </summary>
public interface ISymbolRepository
{
    Task<SymbolDto?> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SymbolDto>> SearchAsync(string query, int limit = 20, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SymbolDto>> GetSymbolsAsync(
        string? exchange = null,
        string? sector = null,
        bool? isActive = null,
        string? cursorSymbol = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SymbolDto>> GetActiveSymbolsAsync(CancellationToken cancellationToken = default);

    Task<SymbolDto> UpsertAsync(SymbolDto symbol, CancellationToken cancellationToken = default);
}

