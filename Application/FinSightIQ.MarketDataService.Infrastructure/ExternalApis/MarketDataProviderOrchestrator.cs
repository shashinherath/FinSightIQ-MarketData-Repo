using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSightIQ.MarketDataService.Infrastructure.ExternalApis;

/// <summary>
/// Orchestrates primary (Alpha Vantage) and fallback (Yahoo Finance) providers,
/// persisting provider health and circuit breaker transitions to provider_health.
/// </summary>
public sealed class MarketDataProviderOrchestrator : IMarketDataProviderOrchestrator
{
    private readonly IReadOnlyList<IMarketDataProvider> _orderedProviders;
    private readonly IProviderHealthRepository _providerHealthRepository;
    private readonly MarketDataOptions _options;
    private readonly ILogger<MarketDataProviderOrchestrator> _logger;

    public MarketDataProviderOrchestrator(
        IEnumerable<IMarketDataProvider> providers,
        IProviderHealthRepository providerHealthRepository,
        IOptions<MarketDataOptions> options,
        ILogger<MarketDataProviderOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(providerHealthRepository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _orderedProviders = OrderProviders(providers);
        _providerHealthRepository = providerHealthRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<OhlcvBarDto>>> FetchBarsWithFallbackAsync(
        string symbol,
        string interval = "1day",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("VALIDATION_FAILED", "Symbol is required."));
        }

        string normalizedSymbol = symbol.Trim().ToUpperInvariant();
        Error? lastError = null;

        foreach (IMarketDataProvider provider in _orderedProviders)
        {
            bool canAttempt = await CanAttemptProviderAsync(provider.ProviderName, cancellationToken);
            if (!canAttempt)
            {
                _logger.LogInformation(
                    "Skipping provider {Provider} for {Symbol} because its circuit breaker is Open.",
                    provider.ProviderName,
                    normalizedSymbol);
                continue;
            }

            Result<IReadOnlyList<OhlcvBarDto>> result;
            try
            {
                result = await provider.GetBarsAsync(normalizedSymbol, interval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provider {Provider} threw an exception while fetching {Symbol} ({Interval}).",
                    provider.ProviderName,
                    normalizedSymbol,
                    interval);

                result = Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_UNAVAILABLE", ex.Message));
            }

            if (result.IsSuccess && result.Value.Count > 0)
            {
                await SafeRecordSuccessAsync(provider.ProviderName, cancellationToken);
                return result;
            }

            lastError = result.Error;
            await SafeRecordFailureAsync(provider.ProviderName, result.Error.Message, cancellationToken);

            _logger.LogWarning(
                "Provider {Provider} failed for {Symbol} ({Interval}) with [{ErrorCode}] {ErrorMessage}. Attempting fallback if available.",
                provider.ProviderName,
                normalizedSymbol,
                interval,
                result.Error.Code,
                result.Error.Message);
        }

        return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
            new Error(
                "UPSTREAM_UNAVAILABLE",
                lastError is not null
                    ? $"All upstream market data providers failed for '{normalizedSymbol}'. Last error: {lastError.Message}"
                    : $"All upstream market data providers are unavailable for '{normalizedSymbol}'."));
    }

    private async Task<bool> CanAttemptProviderAsync(string providerName, CancellationToken cancellationToken)
    {
        try
        {
            ProviderHealthDto? health = await _providerHealthRepository.GetByProviderAsync(providerName, cancellationToken);
            if (health is null)
            {
                return true;
            }

            if (!string.Equals(health.CircuitState, ProviderCircuitState.Open, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int breakSeconds = Math.Max(1, _options.CircuitBreakerBreakSeconds);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (health.LastFailureAt.HasValue &&
                now - health.LastFailureAt.Value >= TimeSpan.FromSeconds(breakSeconds))
            {
                await _providerHealthRepository.UpdateCircuitStateAsync(
                    providerName,
                    ProviderCircuitState.HalfOpen,
                    cancellationToken);

                _logger.LogInformation(
                    "Transitioned provider {Provider} circuit state from Open to HalfOpen after {BreakSeconds}s cooldown.",
                    providerName,
                    breakSeconds);

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read provider health for {Provider}; proceeding with provider call.",
                providerName);
            return true;
        }
    }

    private async Task SafeRecordSuccessAsync(string providerName, CancellationToken cancellationToken)
    {
        try
        {
            await _providerHealthRepository.RecordSuccessAsync(
                providerName,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record provider success for {Provider} in provider_health.",
                providerName);
        }
    }

    private async Task SafeRecordFailureAsync(string providerName, string errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            ProviderHealthDto? existing = await _providerHealthRepository.GetByProviderAsync(providerName, cancellationToken);
            int nextFailures = (existing?.ConsecutiveFailures ?? 0) + 1;
            int threshold = Math.Max(1, _options.CircuitBreakerFailureThreshold);

            string nextState = nextFailures >= threshold ||
                               string.Equals(existing?.CircuitState, ProviderCircuitState.HalfOpen, StringComparison.OrdinalIgnoreCase)
                ? ProviderCircuitState.Open
                : ProviderCircuitState.Closed;

            await _providerHealthRepository.RecordFailureAsync(
                providerName,
                DateTimeOffset.UtcNow,
                errorMessage,
                nextState,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to record provider failure for {Provider} in provider_health.",
                providerName);
        }
    }

    private static IReadOnlyList<IMarketDataProvider> OrderProviders(IEnumerable<IMarketDataProvider> providers)
    {
        return providers
            .OrderBy(p => string.Equals(p.ProviderName, MarketDataProviderNames.AlphaVantage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
