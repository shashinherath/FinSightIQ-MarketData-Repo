using System.Text.Json;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Infrastructure.ExternalApis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSightIQ.MarketDataService.Infrastructure.Caching;

/// <summary>
/// Redis cache-aside implementation backed by <see cref="IDistributedCache"/>.
/// Gracefully degrades on Redis connection or timeout failures without throwing.
/// </summary>
public sealed class RedisMarketDataCache : IMarketDataCache
{
    public const string QuoteKeyPrefix = "finsightiq:market:quote:";
    public const string SymbolKeyPrefix = "finsightiq:market:symbols:";

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDistributedCache _distributedCache;
    private readonly MarketDataOptions _options;
    private readonly ILogger<RedisMarketDataCache> _logger;

    public RedisMarketDataCache(
        IDistributedCache distributedCache,
        IOptions<MarketDataOptions> options,
        ILogger<RedisMarketDataCache> logger)
    {
        ArgumentNullException.ThrowIfNull(distributedCache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _distributedCache = distributedCache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LiveQuoteDto?> GetLiveQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        string normalizedSymbol = symbol.Trim().ToUpperInvariant();
        string cacheKey = $"{QuoteKeyPrefix}{normalizedSymbol}";

        try
        {
            string? cachedJson = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(cachedJson))
            {
                return null;
            }

            LiveQuoteDto? quote = JsonSerializer.Deserialize<LiveQuoteDto>(cachedJson, SerializerOptions);
            if (quote is null)
            {
                return null;
            }

            return quote with { Cached = true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Redis cache read failed for key {CacheKey}; falling back to database.",
                cacheKey);
            return null;
        }
    }

    public async Task SetLiveQuoteAsync(
        LiveQuoteDto quote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quote);

        string normalizedSymbol = quote.Symbol.Trim().ToUpperInvariant();
        string cacheKey = $"{QuoteKeyPrefix}{normalizedSymbol}";
        int ttlSeconds = Math.Max(1, _options.QuoteCacheTtlSeconds);

        try
        {
            LiveQuoteDto payloadToCache = quote with { Symbol = normalizedSymbol, Cached = true };
            string json = JsonSerializer.Serialize(payloadToCache, SerializerOptions);

            DistributedCacheEntryOptions entryOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
            };

            await _distributedCache.SetStringAsync(cacheKey, json, entryOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Redis cache write failed for key {CacheKey}; continuing without caching.",
                cacheKey);
        }
    }

    public async Task<SymbolDto?> GetSymbolAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        string normalizedSymbol = symbol.Trim().ToUpperInvariant();
        string cacheKey = $"{SymbolKeyPrefix}{normalizedSymbol}";

        try
        {
            string? cachedJson = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(cachedJson))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SymbolDto>(cachedJson, SerializerOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Redis symbol metadata cache read failed for key {CacheKey}; falling back to database.",
                cacheKey);
            return null;
        }
    }

    public async Task SetSymbolAsync(
        SymbolDto symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        string normalizedSymbol = symbol.Symbol.Trim().ToUpperInvariant();
        string cacheKey = $"{SymbolKeyPrefix}{normalizedSymbol}";
        int ttlHours = Math.Max(1, _options.SymbolCacheTtlHours);

        try
        {
            string json = JsonSerializer.Serialize(symbol, SerializerOptions);
            DistributedCacheEntryOptions entryOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(ttlHours)
            };

            await _distributedCache.SetStringAsync(cacheKey, json, entryOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Redis symbol metadata cache write failed for key {CacheKey}.",
                cacheKey);
        }
    }
}

