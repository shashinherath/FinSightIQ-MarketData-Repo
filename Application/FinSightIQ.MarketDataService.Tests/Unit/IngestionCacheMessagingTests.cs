using System.Text;
using System.Text.Json;
using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.Contracts.Events;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Services;
using FinSightIQ.MarketDataService.Infrastructure.Caching;
using FinSightIQ.MarketDataService.Infrastructure.ExternalApis;
using FinSightIQ.MarketDataService.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;

namespace FinSightIQ.MarketDataService.Tests.Unit;

public sealed class IngestionCacheMessagingTests
{
    [Fact]
    public void NyseMarketHoursGuard_ReturnsFalse_OnWeekendAndOutsideHoursAndHolidays()
    {
        NyseMarketHoursGuard guard = new NyseMarketHoursGuard();

        // Saturday 2026-01-17 15:00 UTC (10:00 ET) -> Closed
        DateTimeOffset saturdayUtc = new DateTimeOffset(2026, 1, 17, 15, 0, 0, TimeSpan.Zero);
        guard.IsMarketOpen(saturdayUtc).Should().BeFalse();

        // Weekday before 09:30 ET: Wednesday 2026-01-14 14:29 UTC (09:29 EST) -> Closed
        DateTimeOffset beforeOpenUtc = new DateTimeOffset(2026, 1, 14, 14, 29, 0, TimeSpan.Zero);
        guard.IsMarketOpen(beforeOpenUtc).Should().BeFalse();

        // Weekday at 09:30 ET: Wednesday 2026-01-14 14:30 UTC (09:30 EST) -> Open
        DateTimeOffset atOpenUtc = new DateTimeOffset(2026, 1, 14, 14, 30, 0, TimeSpan.Zero);
        guard.IsMarketOpen(atOpenUtc).Should().BeTrue();

        // Weekday after 16:00 ET: Wednesday 2026-01-14 21:01 UTC (16:01 EST) -> Closed
        DateTimeOffset afterCloseUtc = new DateTimeOffset(2026, 1, 14, 21, 1, 0, TimeSpan.Zero);
        guard.IsMarketOpen(afterCloseUtc).Should().BeFalse();

        // NYSE Holiday: MLK Jr. Day (3rd Monday in January 2026 = 2026-01-19) at 15:00 UTC (10:00 EST) -> Closed
        DateTimeOffset mlkDayUtc = new DateTimeOffset(2026, 1, 19, 15, 0, 0, TimeSpan.Zero);
        guard.IsMarketOpen(mlkDayUtc).Should().BeFalse();

        // NYSE Holiday: Good Friday 2026 (2026-04-03) at 14:00 UTC (10:00 EDT) -> Closed
        DateTimeOffset goodFridayUtc = new DateTimeOffset(2026, 4, 3, 14, 0, 0, TimeSpan.Zero);
        guard.IsMarketOpen(goodFridayUtc).Should().BeFalse();
    }

    [Fact]
    public void NyseMarketHoursGuard_HandlesUsDstTransitions_Correctly()
    {
        NyseMarketHoursGuard guard = new NyseMarketHoursGuard();

        // Before US DST Spring Forward (EST = UTC-5): Friday 2026-03-06 13:30 UTC is 08:30 EST -> Closed
        DateTimeOffset winter1330Utc = new DateTimeOffset(2026, 3, 6, 13, 30, 0, TimeSpan.Zero);
        guard.IsMarketOpen(winter1330Utc).Should().BeFalse();

        // After US DST Spring Forward (EDT = UTC-4): Monday 2026-03-09 13:30 UTC is 09:30 EDT -> Open
        DateTimeOffset summer1330Utc = new DateTimeOffset(2026, 3, 9, 13, 30, 0, TimeSpan.Zero);
        guard.IsMarketOpen(summer1330Utc).Should().BeTrue();

        // After US DST Fall Back (EST = UTC-5): Monday 2026-11-02 20:30 UTC is 15:30 EST -> Open
        DateTimeOffset fallBack2030Utc = new DateTimeOffset(2026, 11, 2, 20, 30, 0, TimeSpan.Zero);
        guard.IsMarketOpen(fallBack2030Utc).Should().BeTrue();
    }

    [Fact]
    public async Task PricePollingService_DoesNotCallUpstream_WhenMarketIsClosed()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IMarketHoursGuard hoursGuard = Substitute.For<IMarketHoursGuard>();
        hoursGuard.IsMarketOpen(Arg.Any<DateTimeOffset>()).Returns(false);

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            PollingEnabled = true,
            BypassMarketHoursGuard = false
        });

        PricePollingService service = new PricePollingService(
            scopeFactory,
            hoursGuard,
            options,
            NullLogger<PricePollingService>.Instance);

        int persisted = await service.ExecutePollCycleAsync(DateTimeOffset.UtcNow);

        persisted.Should().Be(0);
        scopeFactory.DidNotReceive().CreateAsyncScope();
    }

    [Fact]
    public async Task PricePollingService_PollsPersistsCachesAndPublishes_WhenMarketIsOpen()
    {
        ISymbolRepository symbolRepo = Substitute.For<ISymbolRepository>();
        symbolRepo.GetActiveSymbolsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new SymbolDto { Symbol = "AAPL", Name = "Apple Inc.", Exchange = "NASDAQ", IsActive = true }
            });

        DateTimeOffset barTime = new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero);
        OhlcvBarDto latestBar = new OhlcvBarDto("AAPL", 185m, 190m, 184m, 189.30m, 120000L, "1day", MarketDataProviderNames.AlphaVantage, barTime);
        OhlcvBarDto previousBar = new OhlcvBarDto("AAPL", 184m, 188m, 183m, 186.95m, 100000L, "1day", MarketDataProviderNames.AlphaVantage, barTime.AddDays(-1));

        IMarketDataProviderOrchestrator orchestrator = Substitute.For<IMarketDataProviderOrchestrator>();
        orchestrator.FetchBarsWithFallbackAsync("AAPL", "1day", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<OhlcvBarDto>>.Success(new[] { latestBar, previousBar }));

        IStockPriceRepository stockPriceRepo = Substitute.For<IStockPriceRepository>();
        stockPriceRepo.BulkUpsertAsync(Arg.Any<IReadOnlyList<OhlcvBarDto>>(), Arg.Any<CancellationToken>())
            .Returns(2);

        IMarketDataCache cache = Substitute.For<IMarketDataCache>();
        IPriceEventPublisher publisher = Substitute.For<IPriceEventPublisher>();

        ServiceCollection services = new ServiceCollection();
        services.AddSingleton(symbolRepo);
        services.AddSingleton(stockPriceRepo);
        services.AddSingleton(orchestrator);
        services.AddSingleton(cache);
        services.AddSingleton(publisher);
        ServiceProvider provider = services.BuildServiceProvider();

        IMarketHoursGuard hoursGuard = Substitute.For<IMarketHoursGuard>();
        hoursGuard.IsMarketOpen(Arg.Any<DateTimeOffset>()).Returns(true);

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            PollingEnabled = true
        });

        PricePollingService pollingService = new PricePollingService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            hoursGuard,
            options,
            NullLogger<PricePollingService>.Instance);

        int persisted = await pollingService.ExecutePollCycleAsync(barTime);

        persisted.Should().Be(2);
        await stockPriceRepo.Received(1).BulkUpsertAsync(
            Arg.Is<IReadOnlyList<OhlcvBarDto>>(b => b.Count == 2),
            Arg.Any<CancellationToken>());

        await cache.Received(1).SetLiveQuoteAsync(
            Arg.Is<LiveQuoteDto>(q => q.Symbol == "AAPL" && q.Price == 189.30m && q.Change == 2.35m),
            Arg.Any<CancellationToken>());

        await publisher.Received(1).PublishPriceUpdatedAsync(
            Arg.Is<PriceUpdated>(e => e.Symbol == "AAPL" && e.Price == 189.30m && e.Volume == 120000L),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RedisMarketDataCache_ReturnsCachedQuote_AndHandlesRedisFailureGracefully()
    {
        IDistributedCache distributedCache = Substitute.For<IDistributedCache>();
        DateTimeOffset asOf = new DateTimeOffset(2026, 1, 15, 15, 30, 0, TimeSpan.Zero);
        LiveQuoteDto expectedQuote = new LiveQuoteDto("AAPL", 189.30m, 2.35m, 1.26m, 54000000L, asOf, true);
        byte[] serializedBytes = JsonSerializer.SerializeToUtf8Bytes(
            expectedQuote,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        distributedCache.GetAsync("finsightiq:market:quote:AAPL", Arg.Any<CancellationToken>())
            .Returns(serializedBytes);

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            QuoteCacheTtlSeconds = 60
        });

        RedisMarketDataCache cache = new RedisMarketDataCache(
            distributedCache,
            options,
            NullLogger<RedisMarketDataCache>.Instance);

        LiveQuoteDto? hit = await cache.GetLiveQuoteAsync("aapl");
        hit.Should().NotBeNull();
        hit!.Symbol.Should().Be("AAPL");
        hit.Price.Should().Be(189.30m);
        hit.Cached.Should().BeTrue();

        // Simulate Redis failure (TC-MKT-12)
        distributedCache.GetAsync("finsightiq:market:quote:MSFT", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Redis connection refused"));

        LiveQuoteDto? missOnOutage = await cache.GetLiveQuoteAsync("MSFT");
        missOnOutage.Should().BeNull();
    }

    [Fact]
    public async Task RabbitMqPriceEventPublisher_PublishesToTopicExchange_WithRoutingKeyAndPayload()
    {
        IConnectionFactory connectionFactory = Substitute.For<IConnectionFactory>();
        IConnection connection = Substitute.For<IConnection>();
        IModel channel = Substitute.For<IModel>();
        IBasicProperties basicProps = Substitute.For<IBasicProperties>();

        connection.IsOpen.Returns(true);
        channel.IsOpen.Returns(true);
        channel.CreateBasicProperties().Returns(basicProps);
        connectionFactory.CreateConnection().Returns(connection);
        connection.CreateModel().Returns(channel);

        IOptions<RabbitMqOptions> options = Options.Create(new RabbitMqOptions
        {
            ExchangeName = "market.price.updated",
            RoutingKey = "market.price.updated"
        });

        IConfiguration configuration = new ConfigurationBuilder().Build();
        using RabbitMqPriceEventPublisher publisher = new RabbitMqPriceEventPublisher(
            options,
            configuration,
            NullLogger<RabbitMqPriceEventPublisher>.Instance,
            connectionFactory);

        PriceUpdated evt = new PriceUpdated
        {
            Symbol = "NVDA",
            Price = 135.50m,
            Volume = 89000000L,
            Timestamp = new DateTimeOffset(2026, 1, 15, 16, 0, 0, TimeSpan.Zero),
            Open = 132.00m,
            High = 136.20m,
            Low = 131.80m
        };

        await publisher.PublishPriceUpdatedAsync(evt, "corr-123");

        channel.Received(1).ExchangeDeclare(
            "market.price.updated",
            ExchangeType.Topic,
            true,
            false,
            null);

        channel.Received(1).BasicPublish(
            "market.price.updated",
            "market.price.updated",
            false,
            basicProps,
            Arg.Is<ReadOnlyMemory<byte>>(body =>
                Encoding.UTF8.GetString(body.ToArray()).Contains("\"symbol\":\"NVDA\"") &&
                Encoding.UTF8.GetString(body.ToArray()).Contains("\"price\":135.50")));
    }
}
