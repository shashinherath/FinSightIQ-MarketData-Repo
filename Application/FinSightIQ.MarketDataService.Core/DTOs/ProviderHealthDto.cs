namespace FinSightIQ.MarketDataService.Core.DTOs;

/// <summary>
/// Circuit breaker states tracked in provider_health.
/// </summary>
public static class ProviderCircuitState
{
    public const string Closed = "Closed";
    public const string Open = "Open";
    public const string HalfOpen = "HalfOpen";
}

/// <summary>
/// Canonical provider names tracked in provider_health.
/// </summary>
public static class MarketDataProviderNames
{
    public const string AlphaVantage = "AlphaVantage";
    public const string YahooFinance = "YahooFinance";
}

/// <summary>
/// Represents a row in the provider_health table.
/// </summary>
public sealed record ProviderHealthDto
{
    public string Provider { get; init; } = string.Empty;
    public int ConsecutiveFailures { get; init; }
    public string CircuitState { get; init; } = ProviderCircuitState.Closed;
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public string? LastError { get; init; }
}

