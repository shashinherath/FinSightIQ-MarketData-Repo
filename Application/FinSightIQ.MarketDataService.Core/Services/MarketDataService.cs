using System.Text;
using System.Text.Json;
using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Validators;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;

namespace FinSightIQ.MarketDataService.Core.Services;

/// <summary>
/// Core domain service implementing historical OHLCV queries, cache-aside live quotes,
/// symbol universe listing, and trigram symbol search.
/// </summary>
public sealed class MarketDataService : IMarketDataService
{
    private static readonly JsonSerializerOptions CursorJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly PriceHistoryQueryValidator HistoryValidator = new PriceHistoryQueryValidator();
    private static readonly SymbolsFilterQueryValidator SymbolsValidator = new SymbolsFilterQueryValidator();

    private readonly ISymbolRepository _symbolRepository;
    private readonly IStockPriceRepository _stockPriceRepository;
    private readonly IMarketDataCache _marketDataCache;
    private readonly IMarketDataProviderOrchestrator _providerOrchestrator;
    private readonly ILogger<MarketDataService> _logger;

    public MarketDataService(
        ISymbolRepository symbolRepository,
        IStockPriceRepository stockPriceRepository,
        IMarketDataCache marketDataCache,
        IMarketDataProviderOrchestrator providerOrchestrator,
        ILogger<MarketDataService> logger)
    {
        ArgumentNullException.ThrowIfNull(symbolRepository);
        ArgumentNullException.ThrowIfNull(stockPriceRepository);
        ArgumentNullException.ThrowIfNull(marketDataCache);
        ArgumentNullException.ThrowIfNull(providerOrchestrator);
        ArgumentNullException.ThrowIfNull(logger);

        _symbolRepository = symbolRepository;
        _stockPriceRepository = stockPriceRepository;
        _marketDataCache = marketDataCache;
        _providerOrchestrator = providerOrchestrator;
        _logger = logger;
    }

    public async Task<Result<PriceHistoryResponse>> GetPriceHistoryAsync(
        PriceHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        ValidationResult validation = HistoryValidator.Validate(query);
        if (!validation.IsValid)
        {
            string message = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<PriceHistoryResponse>.Failure(new Error("VALIDATION_FAILED", message));
        }

        string normalizedSymbol = query.Symbol.Trim().ToUpperInvariant();
        string normalizedInterval = NormalizeInterval(query.Interval);

        DateTimeOffset? cursorRecordedAt = null;
        long? cursorId = null;

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!TryDecodeHistoryCursor(query.Cursor, out cursorRecordedAt, out cursorId))
            {
                return Result<PriceHistoryResponse>.Failure(
                    new Error("VALIDATION_FAILED", "The provided pagination cursor is malformed."));
            }
        }

        SymbolDto? symbolMeta = await GetSymbolWithCacheAsync(normalizedSymbol, cancellationToken);
        if (symbolMeta is null)
        {
            return Result<PriceHistoryResponse>.Failure(
                new Error("SYMBOL_NOT_FOUND", $"Symbol '{normalizedSymbol}' was not found in the tracked equity universe."));
        }

        int fetchLimit = query.Limit + 1;
        IReadOnlyList<StockPriceEntity> entities = await _stockPriceRepository.GetHistoricalPricesAsync(
            normalizedSymbol,
            normalizedInterval,
            query.From,
            query.To,
            cursorRecordedAt,
            cursorId,
            fetchLimit,
            cancellationToken);

        bool hasMore = entities.Count > query.Limit;
        IReadOnlyList<StockPriceEntity> pageEntities = hasMore
            ? entities.Take(query.Limit).ToList()
            : entities;

        string? nextCursor = null;
        if (hasMore && pageEntities.Count > 0)
        {
            StockPriceEntity lastItem = pageEntities[pageEntities.Count - 1];
            nextCursor = EncodeHistoryCursor(lastItem.RecordedAt, lastItem.Id);
        }

        List<HistoricalBarDto> bars = pageEntities
            .Select(e => new HistoricalBarDto(
                e.Open,
                e.High,
                e.Low,
                e.Close,
                e.Volume,
                e.RecordedAt))
            .ToList();

        PaginationMeta pagination = new PaginationMeta(nextCursor, hasMore, query.Limit);
        PriceHistoryResponse response = new PriceHistoryResponse(
            normalizedSymbol,
            normalizedInterval,
            bars,
            pagination);

        return Result<PriceHistoryResponse>.Success(response);
    }

    public async Task<Result<LiveQuoteDto>> GetLiveQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Trim().Length > 10)
        {
            return Result<LiveQuoteDto>.Failure(
                new Error("VALIDATION_FAILED", "A valid ticker symbol (1-10 characters) is required."));
        }

        string normalizedSymbol = symbol.Trim().ToUpperInvariant();

        SymbolDto? symbolMeta = await GetSymbolWithCacheAsync(normalizedSymbol, cancellationToken);
        if (symbolMeta is null)
        {
            return Result<LiveQuoteDto>.Failure(
                new Error("SYMBOL_NOT_FOUND", $"Symbol '{normalizedSymbol}' was not found in the tracked equity universe."));
        }

        // 1. Check Redis cache first (cache-aside)
        LiveQuoteDto? cachedQuote = await _marketDataCache.GetLiveQuoteAsync(normalizedSymbol, cancellationToken);
        if (cachedQuote is not null)
        {
            return Result<LiveQuoteDto>.Success(cachedQuote with { Cached = true });
        }

        // 2. Cache miss -> Query latest persisted bars from PostgreSQL
        IReadOnlyList<StockPriceEntity> latestBars = await _stockPriceRepository.GetLatestPricesAsync(
            normalizedSymbol,
            null,
            2,
            cancellationToken);

        if (latestBars.Count > 0)
        {
            StockPriceEntity current = latestBars[0];
            decimal previousClose = latestBars.Count > 1 ? latestBars[1].Close : current.Open;
            decimal change = decimal.Round(current.Close - previousClose, 4);
            decimal changePercent = previousClose > 0m
                ? decimal.Round((change / previousClose) * 100m, 4)
                : 0m;

            LiveQuoteDto dbQuote = new LiveQuoteDto(
                normalizedSymbol,
                current.Close,
                change,
                changePercent,
                current.Volume,
                current.RecordedAt,
                false);

            await _marketDataCache.SetLiveQuoteAsync(dbQuote, cancellationToken);
            return Result<LiveQuoteDto>.Success(dbQuote);
        }

        // 3. No bars in PostgreSQL -> Attempt on-demand fetch from upstream providers with fallback
        Result<IReadOnlyList<OhlcvBarDto>> upstreamResult = await _providerOrchestrator.FetchBarsWithFallbackAsync(
            normalizedSymbol,
            "1day",
            cancellationToken);

        if (upstreamResult.IsFailure || upstreamResult.Value.Count == 0)
        {
            return Result<LiveQuoteDto>.Failure(
                new Error(
                    "UPSTREAM_UNAVAILABLE",
                    $"No cached or persisted market data is available for '{normalizedSymbol}', and upstream providers are unavailable."));
        }

        IReadOnlyList<OhlcvBarDto> fetchedBars = upstreamResult.Value;
        try
        {
            await _stockPriceRepository.BulkUpsertAsync(fetchedBars, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist on-demand upstream bars for {Symbol}; returning live quote from upstream response.",
                normalizedSymbol);
        }

        OhlcvBarDto latestBar = fetchedBars[0];
        decimal prevClose = fetchedBars.Count > 1 ? fetchedBars[1].Close : latestBar.Open;
        decimal priceChange = decimal.Round(latestBar.Close - prevClose, 4);
        decimal pctChange = prevClose > 0m
            ? decimal.Round((priceChange / prevClose) * 100m, 4)
            : 0m;

        LiveQuoteDto liveQuote = new LiveQuoteDto(
            normalizedSymbol,
            latestBar.Close,
            priceChange,
            pctChange,
            latestBar.Volume,
            latestBar.RecordedAt,
            false);

        await _marketDataCache.SetLiveQuoteAsync(liveQuote, cancellationToken);
        return Result<LiveQuoteDto>.Success(liveQuote);
    }

    public async Task<Result<SymbolSearchResponse>> SearchSymbolsAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return Result<SymbolSearchResponse>.Failure(
                new Error("VALIDATION_FAILED", "Query parameter 'q' is required and must be at least 2 characters long."));
        }

        string trimmedQuery = query.Trim();
        if (trimmedQuery.Length > 100)
        {
            return Result<SymbolSearchResponse>.Failure(
                new Error("VALIDATION_FAILED", "Query parameter 'q' must not exceed 100 characters."));
        }

        IReadOnlyList<SymbolDto> matches = await _symbolRepository.SearchAsync(trimmedQuery, 20, cancellationToken);

        List<SymbolSearchResultDto> results = matches
            .Take(20)
            .Select(m => new SymbolSearchResultDto(
                m.Symbol,
                m.Name,
                m.Exchange,
                m.Sector))
            .ToList();

        return Result<SymbolSearchResponse>.Success(new SymbolSearchResponse(results));
    }

    public async Task<Result<SymbolsListResponse>> GetSymbolsAsync(
        SymbolsFilterQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        ValidationResult validation = SymbolsValidator.Validate(query);
        if (!validation.IsValid)
        {
            string message = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<SymbolsListResponse>.Failure(new Error("VALIDATION_FAILED", message));
        }

        string? cursorSymbol = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!TryDecodeSymbolCursor(query.Cursor, out cursorSymbol))
            {
                return Result<SymbolsListResponse>.Failure(
                    new Error("VALIDATION_FAILED", "The provided pagination cursor is malformed."));
            }
        }

        int fetchLimit = query.Limit + 1;
        IReadOnlyList<SymbolDto> rows = await _symbolRepository.GetSymbolsAsync(
            query.Exchange,
            query.Sector,
            query.Active,
            cursorSymbol,
            fetchLimit,
            cancellationToken);

        bool hasMore = rows.Count > query.Limit;
        IReadOnlyList<SymbolDto> pageRows = hasMore
            ? rows.Take(query.Limit).ToList()
            : rows;

        string? nextCursor = null;
        if (hasMore && pageRows.Count > 0)
        {
            SymbolDto last = pageRows[pageRows.Count - 1];
            nextCursor = EncodeSymbolCursor(last.Symbol);
        }

        PaginationMeta pagination = new PaginationMeta(nextCursor, hasMore, query.Limit);
        return Result<SymbolsListResponse>.Success(new SymbolsListResponse(pageRows, pagination));
    }

    public static string EncodeHistoryCursor(DateTimeOffset recordedAt, long id)
    {
        HistoryCursorPayload payload = new HistoryCursorPayload(recordedAt.ToUniversalTime(), id);
        string json = JsonSerializer.Serialize(payload, CursorJsonOptions);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecodeHistoryCursor(
        string cursor,
        out DateTimeOffset? recordedAt,
        out long? id)
    {
        recordedAt = null;
        id = null;

        try
        {
            byte[] bytes = Base64UrlDecode(cursor.Trim());
            string json = Encoding.UTF8.GetString(bytes);
            HistoryCursorPayload? payload = JsonSerializer.Deserialize<HistoryCursorPayload>(json, CursorJsonOptions);

            if (payload is null || payload.Id <= 0)
            {
                return false;
            }

            recordedAt = payload.RecordedAt.ToUniversalTime();
            id = payload.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string EncodeSymbolCursor(string symbol)
    {
        SymbolCursorPayload payload = new SymbolCursorPayload(symbol.Trim().ToUpperInvariant());
        string json = JsonSerializer.Serialize(payload, CursorJsonOptions);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecodeSymbolCursor(string cursor, out string? symbol)
    {
        symbol = null;
        try
        {
            byte[] bytes = Base64UrlDecode(cursor.Trim());
            string json = Encoding.UTF8.GetString(bytes);
            SymbolCursorPayload? payload = JsonSerializer.Deserialize<SymbolCursorPayload>(json, CursorJsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Symbol))
            {
                return false;
            }

            symbol = payload.Symbol.Trim().ToUpperInvariant();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SymbolDto?> GetSymbolWithCacheAsync(
        string normalizedSymbol,
        CancellationToken cancellationToken)
    {
        SymbolDto? cached = await _marketDataCache.GetSymbolAsync(normalizedSymbol, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        SymbolDto? dbSymbol = await _symbolRepository.GetBySymbolAsync(normalizedSymbol, cancellationToken);
        if (dbSymbol is not null)
        {
            await _marketDataCache.SetSymbolAsync(dbSymbol, cancellationToken);
        }

        return dbSymbol;
    }

    private static string NormalizeInterval(string interval)
    {
        string trimmed = interval.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "1d" or "daily" or "1day" => "1day",
            "1m" or "1min" => "1min",
            "5m" or "5min" => "5min",
            "15m" or "15min" => "15min",
            "30m" or "30min" => "30min",
            "60m" or "1h" or "60min" => "60min",
            _ => trimmed
        };
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        string base64 = input
            .Replace('-', '+')
            .Replace('_', '/');

        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
        }

        return Convert.FromBase64String(base64);
    }

    private sealed record HistoryCursorPayload(DateTimeOffset RecordedAt, long Id);

    private sealed record SymbolCursorPayload(string Symbol);
}

