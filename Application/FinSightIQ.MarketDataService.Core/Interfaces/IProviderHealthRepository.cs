using FinSightIQ.MarketDataService.Core.DTOs;

namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Repository contract for tracking upstream market data provider health and circuit breaker states.
/// </summary>
public interface IProviderHealthRepository
{
    Task<ProviderHealthDto?> GetByProviderAsync(
        string provider,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderHealthDto>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task RecordSuccessAsync(
        string provider,
        DateTimeOffset succeededAt,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        string provider,
        DateTimeOffset failedAt,
        string errorMessage,
        string? circuitState = null,
        CancellationToken cancellationToken = default);

    Task UpdateCircuitStateAsync(
        string provider,
        string circuitState,
        CancellationToken cancellationToken = default);
}

