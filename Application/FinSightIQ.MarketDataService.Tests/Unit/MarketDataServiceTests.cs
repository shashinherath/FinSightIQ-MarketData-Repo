using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FinSightIQ.MarketDataService.Tests.Unit;

public sealed class MarketDataServiceTests
{
    private readonly ISymbolRepository _symbolRepo = Substitute.For<ISymbolRepository>();
    private readonly IStockPriceRepository _stockPriceRepo = Substitute.For<IStockPriceRepository>();
    private readonly IMarketDataCache _cache = Substitute.For<IMarketDataCache>();
    private readonly IMarketDataProviderOrchestrator _orchestrator = Substitute.For<IMarketDataProviderOrchestrator>();
    private readonly Core.Services.MarketDataService _sut;

    public MarketDataServiceTests()
    {
        _sut = new Core.Services.MarketDataService(
            _symbolRepo,
            _stockPriceRepo,
            _cache,
            _orchestrator,
            NullLogger<Core.Services.MarketDataService>.Instance);
    }

    [Fact]
    public async Task GetLiveQuoteAsync_WhenCacheHit_ReturnsCachedQuoteWithoutQueryingDatabase()
    {
        SymbolDto symbol = new SymbolDto { Symbol = "AAPL", Name = "Apple Inc.", Exchange = "NASDAQ", IsActive = true };
        _cache.GetSymbolAsync("AAPL", Arg.Any<CancellationToken>()).Returns(symbol);

        DateTimeOffset asOf = new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero);
        LiveQuoteDto cachedQuote = new LiveQuoteDto("AAPL", 189.30m, 2.35m, 1.26m, 54000000L, asOf, true);
        _cache.GetLiveQuoteAsync("AAPL", Arg.Any<CancellationToken>()).Returns(cachedQuote);

        Result<LiveQuoteDto> result = await _sut.GetLiveQuoteAsync("aapl");

        result.IsSuccess.Should().BeTrue();
        result.Value.Cached.Should().BeTrue();
        result.Value.Price.Should().Be(189.30m);

        await _stockPriceRepo.DidNotReceive().GetLatestPricesAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLiveQuoteAsync_WhenCacheMiss_QueriesDatabaseAndPopulatesCache()
    {
        SymbolDto symbol = new SymbolDto { Symbol = "AAPL", Name = "Apple Inc.", Exchange = "NASDAQ", IsActive = true };
        _symbolRepo.GetBySymbolAsync("AAPL", Arg.Any<CancellationToken>()).Returns(symbol);
        _cache.GetLiveQuoteAsync("AAPL", Arg.Any<CancellationToken>()).Returns((LiveQuoteDto?)null);

        DateTimeOffset latestTime = new DateTimeOffset(2026, 1, 15, 16, 0, 0, TimeSpan.Zero);
        StockPriceEntity latest = new StockPriceEntity
        {
            Id = 2,
            Symbol = "AAPL",
            Open = 187.0m,
            High = 190.0m,
            Low = 186.5m,
            Close = 189.30m,
            Volume = 54230100L,
            Interval = "1day",
            Source = MarketDataProviderNames.AlphaVantage,
            RecordedAt = latestTime
        };
        StockPriceEntity previous = new StockPriceEntity
        {
            Id = 1,
            Symbol = "AAPL",
            Open = 185.0m,
            High = 187.5m,
            Low = 184.5m,
            Close = 186.95m,
            Volume = 48000000L,
            Interval = "1day",
            Source = MarketDataProviderNames.AlphaVantage,
            RecordedAt = latestTime.AddDays(-1)
        };

        _stockPriceRepo.GetLatestPricesAsync("AAPL", null, 2, Arg.Any<CancellationToken>())
            .Returns(new[] { latest, previous });

        Result<LiveQuoteDto> result = await _sut.GetLiveQuoteAsync("AAPL");

        result.IsSuccess.Should().BeTrue();
        result.Value.Cached.Should().BeFalse();
        result.Value.Price.Should().Be(189.30m);
        result.Value.Change.Should().Be(2.35m);

        await _cache.Received(1).SetLiveQuoteAsync(
            Arg.Is<LiveQuoteDto>(q => q.Symbol == "AAPL" && q.Price == 189.30m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLiveQuoteAsync_WhenUnknownSymbol_ReturnsSymbolNotFound()
    {
        _symbolRepo.GetBySymbolAsync("ZZZZ", Arg.Any<CancellationToken>()).Returns((SymbolDto?)null);

        Result<LiveQuoteDto> result = await _sut.GetLiveQuoteAsync("ZZZZ");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public async Task GetPriceHistoryAsync_WhenFromGreaterThanTo_ReturnsValidationFailed()
    {
        DateTimeOffset from = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        PriceHistoryQuery query = new PriceHistoryQuery("AAPL", from, to, "1day", 50, null);

        Result<PriceHistoryResponse> result = await _sut.GetPriceHistoryAsync(query);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("VALIDATION_FAILED");
    }

    [Fact]
    public async Task GetPriceHistoryAsync_ComputesNextCursor_WhenMoreRowsExist()
    {
        SymbolDto symbol = new SymbolDto { Symbol = "AAPL", Name = "Apple Inc.", Exchange = "NASDAQ", IsActive = true };
        _cache.GetSymbolAsync("AAPL", Arg.Any<CancellationToken>()).Returns(symbol);

        DateTimeOffset t1 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t2 = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t3 = new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);

        StockPriceEntity[] rows = new[]
        {
            new StockPriceEntity { Id = 10, Symbol = "AAPL", Open = 180m, High = 182m, Low = 179m, Close = 181m, Volume = 100L, Interval = "1day", RecordedAt = t1 },
            new StockPriceEntity { Id = 11, Symbol = "AAPL", Open = 181m, High = 183m, Low = 180m, Close = 182m, Volume = 200L, Interval = "1day", RecordedAt = t2 },
            new StockPriceEntity { Id = 12, Symbol = "AAPL", Open = 182m, High = 184m, Low = 181m, Close = 183m, Volume = 300L, Interval = "1day", RecordedAt = t3 }
        };

        _stockPriceRepo.GetHistoricalPricesAsync("AAPL", "1day", null, null, null, null, 3, Arg.Any<CancellationToken>())
            .Returns(rows);

        PriceHistoryQuery query = new PriceHistoryQuery("AAPL", null, null, "1day", 2, null);
        Result<PriceHistoryResponse> result = await _sut.GetPriceHistoryAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Should().HaveCount(2);
        result.Value.Pagination.HasMore.Should().BeTrue();
        result.Value.Pagination.NextCursor.Should().NotBeNullOrWhiteSpace();

        bool decoded = Core.Services.MarketDataService.TryDecodeHistoryCursor(
            result.Value.Pagination.NextCursor!,
            out DateTimeOffset? cursorTime,
            out long? cursorId);

        decoded.Should().BeTrue();
        cursorTime.Should().Be(t2);
        cursorId.Should().Be(11L);
    }

    [Fact]
    public async Task SearchSymbolsAsync_RejectsShortQuery_AndReturnsMatchesForValidQuery()
    {
        Result<SymbolSearchResponse> shortResult = await _sut.SearchSymbolsAsync("A");
        shortResult.IsFailure.Should().BeTrue();
        shortResult.Error.Code.Should().Be("VALIDATION_FAILED");

        _symbolRepo.SearchAsync("App", 20, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new SymbolDto { Symbol = "AAPL", Name = "Apple Inc.", Exchange = "NASDAQ", Sector = "Technology", IsActive = true }
            });

        Result<SymbolSearchResponse> validResult = await _sut.SearchSymbolsAsync("App");
        validResult.IsSuccess.Should().BeTrue();
        validResult.Value.Results.Should().ContainSingle(r => r.Symbol == "AAPL" && r.Name == "Apple Inc.");
    }
}

