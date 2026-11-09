using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FinSightIQ.MarketDataService.Api.Health;

/// <summary>
/// Readiness health check verifying Redis distributed cache connectivity.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private const string HealthProbeKey = "finsightiq:market:health:probe";

    private readonly IDistributedCache _distributedCache;
    private readonly ILogger<RedisHealthCheck> _logger;

    public RedisHealthCheck(IDistributedCache distributedCache, ILogger<RedisHealthCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(distributedCache);
        ArgumentNullException.ThrowIfNull(logger);

        _distributedCache = distributedCache;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await _distributedCache.GetStringAsync(HealthProbeKey, cancellationToken);
            return HealthCheckResult.Healthy("Redis distributed cache is reachable.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis readiness health check failed.");
            return HealthCheckResult.Unhealthy("Redis distributed cache is unreachable.", ex);
        }
    }
}

