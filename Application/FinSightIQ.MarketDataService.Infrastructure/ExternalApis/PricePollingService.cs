using FinSightIQ.BuildingBlocks.Middleware;
using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.Contracts.Events;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Validators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSightIQ.MarketDataService.Infrastructure.ExternalApis;

/// <summary>
/// Background service that polls upstream market data providers at a 60-second cadence
/// during NYSE regular trading hours (09:30–16:00 ET, Mon–Fri, excluding NYSE holidays),
/// bulk-persists OHLCV bars via NpgsqlBinaryImporter, updates Redis cache, and publishes PriceUpdated events.
/// </summary>
public sealed class PricePollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMarketHoursGuard _marketHoursGuard;
    private readonly MarketDataOptions _options;
    private readonly ILogger<PricePollingService> _logger;

    public PricePollingService(
        IServiceScopeFactory scopeFactory,
        IMarketHoursGuard marketHoursGuard,
        IOptions<MarketDataOptions> options,
        ILogger<PricePollingService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(marketHoursGuard);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _marketHoursGuard = marketHoursGuard;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        if (!_options.PollingEnabled)
        {
            _logger.LogInformation("PricePollingService is disabled by configuration (MarketData:PollingEnabled = false).");
            return;
        }

        int intervalSeconds = Math.Max(5, _options.PollingIntervalSeconds);
        _logger.LogInformation(
            "PricePollingService started with polling cadence of {IntervalSeconds}s (BypassMarketHoursGuard={Bypass}).",
            intervalSeconds,
            _options.BypassMarketHoursGuard);

        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        try
        {
            do
            {
                await SafeExecutePollCycleAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("PricePollingService is stopping gracefully.");
        }
    }

    /// <summary>
    /// Executes a single market data polling cycle if polling is enabled and the market is open.
    /// Returns the number of OHLCV bars persisted.
    /// </summary>
    public async Task<int> ExecutePollCycleAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!_options.PollingEnabled)
        {
            return 0;
        }

        if (!_options.BypassMarketHoursGuard && !_marketHoursGuard.IsMarketOpen(utcNow))
        {
            _logger.LogDebug(
                "Skipping market data poll cycle at {UtcNow}: US equity market is closed.",
                utcNow);
            return 0;
        }

        string cycleCorrelationId = CorrelationId.NewId().Value;
        using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ISymbolRepository symbolRepository = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
        IStockPriceRepository stockPriceRepository = scope.ServiceProvider.GetRequiredService<IStockPriceRepository>();
        IMarketDataProviderOrchestrator orchestrator = scope.ServiceProvider.GetRequiredService<IMarketDataProviderOrchestrator>();
        IMarketDataCache marketDataCache = scope.ServiceProvider.GetRequiredService<IMarketDataCache>();
        IPriceEventPublisher eventPublisher = scope.ServiceProvider.GetRequiredService<IPriceEventPublisher>();

        await EnsureUpcomingPartitionsAsync(stockPriceRepository, utcNow, cancellationToken);

        IReadOnlyList<SymbolDto> activeSymbols = await symbolRepository.GetActiveSymbolsAsync(cancellationToken);
        if (activeSymbols.Count == 0)
        {
            _logger.LogDebug("No active symbols found in marketdb during poll cycle {CorrelationId}.", cycleCorrelationId);
            return 0;
        }

        List<OhlcvBarDto> validatedBatch = new List<OhlcvBarDto>();
        List<(LiveQuoteDto Quote, OhlcvBarDto LatestBar)> symbolUpdates = new List<(LiveQuoteDto Quote, OhlcvBarDto LatestBar)>();

        foreach (SymbolDto symbol in activeSymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<IReadOnlyList<OhlcvBarDto>> providerResult = await orchestrator.FetchBarsWithFallbackAsync(
                symbol.Symbol,
                "1day",
                cancellationToken);

            if (providerResult.IsFailure || providerResult.Value.Count == 0)
            {
                continue;
            }

            List<OhlcvBarDto> symbolValidBars = new List<OhlcvBarDto>();
            foreach (OhlcvBarDto bar in providerResult.Value)
            {
                if (OhlcvBarValidator.IsValid(bar, out string? failureReason))
                {
                    symbolValidBars.Add(bar);
                    validatedBatch.Add(bar);
                }
                else
                {
                    _logger.LogWarning(
                        "PricePollingService rejected invalid bar for {Symbol} at {RecordedAt}: {Reason}",
                        symbol.Symbol,
                        bar.RecordedAt,
                        failureReason);
                }
            }

            if (symbolValidBars.Count > 0)
            {
                List<OhlcvBarDto> ordered = symbolValidBars
                    .OrderByDescending(b => b.RecordedAt)
                    .ToList();

                OhlcvBarDto latest = ordered[0];
                decimal previousClose = ordered.Count > 1 ? ordered[1].Close : latest.Open;
                decimal change = decimal.Round(latest.Close - previousClose, 4);
                decimal changePercent = previousClose > 0m
                    ? decimal.Round((change / previousClose) * 100m, 4)
                    : 0m;

                LiveQuoteDto liveQuote = new LiveQuoteDto(
                    symbol.Symbol,
                    latest.Close,
                    change,
                    changePercent,
                    latest.Volume,
                    latest.RecordedAt,
                    true);

                symbolUpdates.Add((liveQuote, latest));
            }
        }

        if (validatedBatch.Count == 0)
        {
            return 0;
        }

        int persistedCount = await stockPriceRepository.BulkUpsertAsync(validatedBatch, cancellationToken);

        foreach ((LiveQuoteDto quote, OhlcvBarDto latestBar) in symbolUpdates)
        {
            await marketDataCache.SetLiveQuoteAsync(quote, cancellationToken);

            PriceUpdated priceEvent = new PriceUpdated
            {
                Symbol = quote.Symbol,
                Price = quote.Price,
                Volume = quote.Volume,
                Timestamp = quote.AsOf,
                Open = latestBar.Open,
                High = latestBar.High,
                Low = latestBar.Low
            };

            await eventPublisher.PublishPriceUpdatedAsync(priceEvent, cycleCorrelationId, cancellationToken);
        }

        _logger.LogInformation(
            "Completed market data poll cycle {CorrelationId}: persisted {PersistedCount} bars across {SymbolCount} symbols.",
            cycleCorrelationId,
            persistedCount,
            symbolUpdates.Count);

        return persistedCount;
    }

    private async Task SafeExecutePollCycleAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecutePollCycleAsync(utcNow, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled exception occurred during PricePollingService cycle at {UtcNow}.",
                utcNow);
        }
    }

    private async Task EnsureUpcomingPartitionsAsync(
        IStockPriceRepository stockPriceRepository,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        try
        {
            DateTime utcDate = utcNow.UtcDateTime;
            for (int monthOffset = 0; monthOffset <= 3; monthOffset++)
            {
                DateTime targetMonth = new DateTime(utcDate.Year, utcDate.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMonths(monthOffset);

                await stockPriceRepository.EnsurePartitionExistsAsync(
                    targetMonth.Year,
                    targetMonth.Month,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to ensure upcoming monthly partitions for stock_prices.");
        }
    }
}
