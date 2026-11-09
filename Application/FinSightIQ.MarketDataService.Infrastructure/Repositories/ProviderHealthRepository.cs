using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;

namespace FinSightIQ.MarketDataService.Infrastructure.Repositories;

/// <summary>
/// Dapper repository implementation for the provider_health table.
/// </summary>
public sealed class ProviderHealthRepository : IProviderHealthRepository
{
    private readonly IDapperWrapper _dapper;

    public ProviderHealthRepository(IDapperWrapper dapper)
    {
        ArgumentNullException.ThrowIfNull(dapper);
        _dapper = dapper;
    }

    public async Task<ProviderHealthDto?> GetByProviderAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        const string sql = "SELECT * FROM fn_GetProviderHealth(@p_provider);";
        return await _dapper.QuerySingleOrDefaultAsync<ProviderHealthDto>(
            sql,
            new { p_provider = provider.Trim() },
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderHealthDto>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT * FROM fn_GetAllProviderHealth();";
        IEnumerable<ProviderHealthDto> rows = await _dapper.QueryAsync<ProviderHealthDto>(
            sql,
            cancellationToken: cancellationToken);

        return rows.ToList();
    }

    public async Task RecordSuccessAsync(
        string provider,
        DateTimeOffset succeededAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        const string sql = "CALL sp_RecordProviderSuccess(@p_provider, @p_succeeded_at);";
        await _dapper.ExecuteAsync(
            sql,
            new
            {
                p_provider = provider.Trim(),
                p_succeeded_at = succeededAt.ToUniversalTime()
            },
            cancellationToken: cancellationToken);
    }

    public async Task RecordFailureAsync(
        string provider,
        DateTimeOffset failedAt,
        string errorMessage,
        string? circuitState = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        const string sql = "CALL sp_RecordProviderFailure(@p_provider, @p_failed_at, @p_error, @p_circuit_state);";
        await _dapper.ExecuteAsync(
            sql,
            new
            {
                p_provider = provider.Trim(),
                p_failed_at = failedAt.ToUniversalTime(),
                p_error = errorMessage,
                p_circuit_state = circuitState
            },
            cancellationToken: cancellationToken);
    }

    public async Task UpdateCircuitStateAsync(
        string provider,
        string circuitState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(circuitState);

        const string sql = "CALL sp_UpdateProviderCircuitState(@p_provider, @p_circuit_state);";
        await _dapper.ExecuteAsync(
            sql,
            new
            {
                p_provider = provider.Trim(),
                p_circuit_state = circuitState.Trim()
            },
            cancellationToken: cancellationToken);
    }
}

