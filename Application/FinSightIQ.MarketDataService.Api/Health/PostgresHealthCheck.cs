using FinSightIQ.MarketDataService.Core.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FinSightIQ.MarketDataService.Api.Health;

/// <summary>
/// Readiness health check verifying PostgreSQL (marketdb) connectivity.
/// </summary>
public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly IDapperWrapper _dapper;
    private readonly ILogger<PostgresHealthCheck> _logger;

    public PostgresHealthCheck(IDapperWrapper dapper, ILogger<PostgresHealthCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(dapper);
        ArgumentNullException.ThrowIfNull(logger);

        _dapper = dapper;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            int result = await _dapper.QuerySingleOrDefaultAsync<int>(
                "SELECT 1;",
                commandTimeout: 5,
                cancellationToken: cancellationToken);

            return result == 1
                ? HealthCheckResult.Healthy("PostgreSQL marketdb is reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL marketdb returned unexpected result.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostgreSQL readiness health check failed.");
            return HealthCheckResult.Unhealthy("PostgreSQL marketdb is unreachable.", ex);
        }
    }
}

