using Dapper;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Infrastructure.Repositories;
using FluentAssertions;
using Npgsql;

namespace FinSightIQ.MarketDataService.Tests.Integration;

public sealed class MarketDatabaseIntegrationTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;
    private readonly SymbolRepository _symbolRepository;
    private readonly StockPriceRepository _stockPriceRepository;
    private readonly ProviderHealthRepository _providerHealthRepository;

    public MarketDatabaseIntegrationTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
        _symbolRepository = new SymbolRepository(fixture.Dapper);
        _stockPriceRepository = new StockPriceRepository(fixture.Dapper);
        _providerHealthRepository = new ProviderHealthRepository(fixture.Dapper);
    }

    [Fact]
    public async Task SymbolRepository_TrigramSearch_FindsAppleInc_AndUpsertsSymbol()
    {
        // TC-MKT-15: Search uses pg_trgm GIN index on symbols.name
        IReadOnlyList<SymbolDto> results = await _symbolRepository.SearchAsync("App", 20);

        results.Should().NotBeEmpty();
        results.Should().Contain(s => s.Symbol == "AAPL" && s.Name == "Apple Inc.");

        SymbolDto? exact = await _symbolRepository.GetBySymbolAsync("msft");
        exact.Should().NotBeNull();
        exact!.Symbol.Should().Be("MSFT");
        exact.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task StockPriceRepository_PartitionRouting_IdempotentUpsert_AndBulkCopyWork()
    {
        // TC-MKT-07: Insert into stock_prices for 2026-01 -> lands in stock_prices_2026_01 partition
        DateTimeOffset recordedAt = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        OhlcvBarDto initialBar = new OhlcvBarDto(
            "AAPL",
            185.00m,
            188.00m,
            184.50m,
            187.25m,
            45000000L,
            "1day",
            MarketDataProviderNames.AlphaVantage,
            recordedAt);

        StockPriceEntity firstInsert = await _stockPriceRepository.UpsertAsync(initialBar);
        firstInsert.Id.Should().BeGreaterThan(0L);
        firstInsert.Close.Should().Be(187.25m);

        // Verify row landed in child partition stock_prices_2026_01
        await using NpgsqlConnection conn = await _fixture.DataSource.OpenConnectionAsync();
        string? partitionName = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT tableoid::regclass::text FROM stock_prices WHERE id = @Id AND recorded_at = @RecordedAt;",
            new { firstInsert.Id, RecordedAt = recordedAt });

        partitionName.Should().Be("stock_prices_2026_01");

        // TC-MKT-09: Re-ingest identical (symbol, interval, recorded_at) row -> ON CONFLICT DO UPDATE updates row; no duplicate
        OhlcvBarDto updatedBar = initialBar with { Close = 187.90m, Volume = 46000000L };
        StockPriceEntity secondUpsert = await _stockPriceRepository.UpsertAsync(updatedBar);

        secondUpsert.Id.Should().Be(firstInsert.Id);
        secondUpsert.Close.Should().Be(187.90m);
        secondUpsert.Volume.Should().Be(46000000L);

        // BulkUpsertAsync using NpgsqlBinaryImporter (COPY)
        List<OhlcvBarDto> batch = new List<OhlcvBarDto>
        {
            new OhlcvBarDto("MSFT", 410m, 415m, 409m, 414m, 20000000L, "1day", MarketDataProviderNames.YahooFinance, recordedAt),
            new OhlcvBarDto("MSFT", 414m, 418m, 412m, 417m, 22000000L, "1day", MarketDataProviderNames.YahooFinance, recordedAt.AddDays(1))
        };

        int affected = await _stockPriceRepository.BulkUpsertAsync(batch);
        affected.Should().Be(2);

        IReadOnlyList<StockPriceEntity> latestMsft = await _stockPriceRepository.GetLatestPricesAsync("MSFT", "1day", 2);
        latestMsft.Should().HaveCount(2);
        latestMsft[0].Close.Should().Be(417m);
        latestMsft[1].Close.Should().Be(414m);
    }

    [Fact]
    public async Task StockPriceRepository_RaisesPostgresError_WhenInsertingOutsidePartitionOrViolatingCheckConstraint()
    {
        // TC-MKT-08: Insert with recorded_at outside existing partitions (e.g., 1995-05-01) -> PostgreSQL raises 23514 error
        DateTimeOffset unpartitionedDate = new DateTimeOffset(1995, 5, 1, 0, 0, 0, TimeSpan.Zero);
        OhlcvBarDto outOfRangeBar = new OhlcvBarDto(
            "AAPL",
            10.0m,
            11.0m,
            9.5m,
            10.5m,
            1000L,
            "1day",
            MarketDataProviderNames.AlphaVantage,
            unpartitionedDate);

        Func<Task> unpartitionedAct = async () => await _stockPriceRepository.UpsertAsync(outOfRangeBar);
        await unpartitionedAct.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == "23514");

        // TC-MKT-04 (DB level): High < Low violates ck_prices_hl (23514)
        DateTimeOffset validDate = new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero);
        OhlcvBarDto invalidOhlc = new OhlcvBarDto(
            "AAPL",
            185.0m,
            180.0m,
            184.0m,
            182.0m,
            1000L,
            "1day",
            MarketDataProviderNames.AlphaVantage,
            validDate);

        Func<Task> checkViolationAct = async () => await _stockPriceRepository.UpsertAsync(invalidOhlc);
        await checkViolationAct.Should().ThrowAsync<PostgresException>()
            .Where(ex => ex.SqlState == "23514");
    }

    [Fact]
    public async Task StockPriceRepository_CursorPagination_WalksMultiplePagesWithoutDuplicatesOrSkips()
    {
        // TC-MKT-13: Walk 3 pages via (recorded_at, id) cursor while inserting new rows
        DateTimeOffset startDay = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        List<OhlcvBarDto> bars = Enumerable.Range(0, 6)
            .Select(i => new OhlcvBarDto(
                "NVDA",
                100m + i,
                105m + i,
                99m + i,
                102m + i,
                100000L + i,
                "1day",
                MarketDataProviderNames.AlphaVantage,
                startDay.AddDays(i)))
            .ToList();

        await _stockPriceRepository.BulkUpsertAsync(bars);

        // Page 1 (limit 2, fetch 3)
        IReadOnlyList<StockPriceEntity> page1 = await _stockPriceRepository.GetHistoricalPricesAsync(
            "NVDA", "1day", startDay, startDay.AddDays(10), null, null, 2);
        page1.Should().HaveCount(2);

        StockPriceEntity cursor1 = page1[page1.Count - 1];

        // Insert a newer row while paginating
        await _stockPriceRepository.UpsertAsync(new OhlcvBarDto(
            "NVDA", 120m, 125m, 119m, 122m, 500000L, "1day", MarketDataProviderNames.AlphaVantage, startDay.AddDays(6)));

        // Page 2
        IReadOnlyList<StockPriceEntity> page2 = await _stockPriceRepository.GetHistoricalPricesAsync(
            "NVDA", "1day", startDay, startDay.AddDays(10), cursor1.RecordedAt, cursor1.Id, 2);
        page2.Should().HaveCount(2);

        StockPriceEntity cursor2 = page2[page2.Count - 1];

        // Page 3
        IReadOnlyList<StockPriceEntity> page3 = await _stockPriceRepository.GetHistoricalPricesAsync(
            "NVDA", "1day", startDay, startDay.AddDays(10), cursor2.RecordedAt, cursor2.Id, 2);
        page3.Should().HaveCount(2);

        List<long> allIds = page1.Concat(page2).Concat(page3).Select(x => x.Id).ToList();
        allIds.Should().OnlyHaveUniqueItems();
        allIds.Should().HaveCount(6);
    }

    [Fact]
    public async Task ProviderHealthRepository_TracksFailuresAndCircuitBreakerTransitions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _providerHealthRepository.RecordFailureAsync(
            MarketDataProviderNames.AlphaVantage,
            now,
            "HTTP 429 Too Many Requests",
            ProviderCircuitState.Open);

        ProviderHealthDto? failedState = await _providerHealthRepository.GetByProviderAsync(MarketDataProviderNames.AlphaVantage);
        failedState.Should().NotBeNull();
        failedState!.CircuitState.Should().Be(ProviderCircuitState.Open);
        failedState.ConsecutiveFailures.Should().BeGreaterThanOrEqualTo(1);
        failedState.LastError.Should().Contain("429");

        await _providerHealthRepository.RecordSuccessAsync(
            MarketDataProviderNames.AlphaVantage,
            now.AddSeconds(35));

        ProviderHealthDto? recoveredState = await _providerHealthRepository.GetByProviderAsync(MarketDataProviderNames.AlphaVantage);
        recoveredState.Should().NotBeNull();
        recoveredState!.CircuitState.Should().Be(ProviderCircuitState.Closed);
        recoveredState.ConsecutiveFailures.Should().Be(0);
        recoveredState.LastError.Should().BeNull();
    }
}

