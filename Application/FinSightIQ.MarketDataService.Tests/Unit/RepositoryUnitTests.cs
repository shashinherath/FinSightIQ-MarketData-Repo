using System.Data;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Infrastructure.Repositories;
using FluentAssertions;
using NSubstitute;

namespace FinSightIQ.MarketDataService.Tests.Unit;

public sealed class RepositoryUnitTests
{
    [Fact]
    public async Task SymbolRepository_GetBySymbolAsync_NormalizesSymbolToUppercase()
    {
        IDapperWrapper dapper = Substitute.For<IDapperWrapper>();
        SymbolDto expected = new SymbolDto
        {
            Symbol = "AAPL",
            Name = "Apple Inc.",
            Exchange = "NASDAQ",
            Sector = "Technology",
            Currency = "USD",
            IsActive = true
        };

        dapper.QuerySingleOrDefaultAsync<SymbolDto>(
            Arg.Is<string>(s => s.Contains("fn_GetSymbolByCode")),
            Arg.Any<object?>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<int?>(),
            Arg.Any<CommandType?>(),
            Arg.Any<CancellationToken>())
            .Returns(expected);

        SymbolRepository repository = new SymbolRepository(dapper);
        SymbolDto? actual = await repository.GetBySymbolAsync(" aapl ");

        actual.Should().NotBeNull();
        actual!.Symbol.Should().Be("AAPL");
    }

    [Fact]
    public async Task SymbolRepository_SearchAsync_ClampsLimitToMax20()
    {
        IDapperWrapper dapper = Substitute.For<IDapperWrapper>();
        dapper.QueryAsync<SymbolDto>(
            Arg.Is<string>(s => s.Contains("fn_SearchSymbols")),
            Arg.Any<object?>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<int?>(),
            Arg.Any<CommandType?>(),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SymbolDto>());

        SymbolRepository repository = new SymbolRepository(dapper);
        IReadOnlyList<SymbolDto> results = await repository.SearchAsync("App", 100);

        results.Should().BeEmpty();
        await dapper.Received(1).QueryAsync<SymbolDto>(
            Arg.Any<string>(),
            Arg.Any<object?>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<int?>(),
            Arg.Any<CommandType?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StockPriceRepository_UpsertAsync_InvokesStoredFunctionWithNormalizedSymbol()
    {
        IDapperWrapper dapper = Substitute.For<IDapperWrapper>();
        DateTimeOffset recordedAt = new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);
        StockPriceEntity expected = new StockPriceEntity
        {
            Id = 42,
            Symbol = "MSFT",
            Open = 400.10m,
            High = 405.00m,
            Low = 399.50m,
            Close = 403.25m,
            Volume = 1500000L,
            Interval = "1day",
            Source = MarketDataProviderNames.AlphaVantage,
            RecordedAt = recordedAt,
            IngestedAt = DateTimeOffset.UtcNow
        };

        dapper.QuerySingleOrDefaultAsync<StockPriceEntity>(
            Arg.Is<string>(s => s.Contains("fn_UpsertStockPrice")),
            Arg.Any<object?>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<int?>(),
            Arg.Any<CommandType?>(),
            Arg.Any<CancellationToken>())
            .Returns(expected);

        StockPriceRepository repository = new StockPriceRepository(dapper);
        OhlcvBarDto bar = new OhlcvBarDto(
            " msft ",
            400.10m,
            405.00m,
            399.50m,
            403.25m,
            1500000L,
            "1day",
            MarketDataProviderNames.AlphaVantage,
            recordedAt);

        StockPriceEntity actual = await repository.UpsertAsync(bar);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task ProviderHealthRepository_RecordFailureAsync_CallsStoredProcedure()
    {
        IDapperWrapper dapper = Substitute.For<IDapperWrapper>();
        ProviderHealthRepository repository = new ProviderHealthRepository(dapper);

        await repository.RecordFailureAsync(
            MarketDataProviderNames.AlphaVantage,
            DateTimeOffset.UtcNow,
            "HTTP 429 Too Many Requests",
            ProviderCircuitState.Open);

        await dapper.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("sp_RecordProviderFailure")),
            Arg.Any<object?>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<int?>(),
            Arg.Any<CommandType?>(),
            Arg.Any<CancellationToken>());
    }
}

