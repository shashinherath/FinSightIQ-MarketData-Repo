using System.Data;
using System.Net;
using System.Text.Json;
using FinSightIQ.BuildingBlocks.Middleware;
using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Infrastructure.ExternalApis;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FinSightIQ.MarketDataService.Tests.Contract;

public sealed class MarketApiContractTests
{
    [Fact]
    public async Task GetHistory_Returns200OkWithSchema_AndPropagatesCorrelationId()
    {
        IMarketDataService marketService = Substitute.For<IMarketDataService>();
        DateTimeOffset recordedAt = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        PriceHistoryResponse expectedResponse = new PriceHistoryResponse(
            "AAPL",
            "1day",
            new[]
            {
                new HistoricalBarDto(185.10m, 187.45m, 184.20m, 186.80m, 54230100L, recordedAt)
            },
            new PaginationMeta(null, false, 50));

        marketService.GetPriceHistoryAsync(
            Arg.Is<PriceHistoryQuery>(q => q.Symbol == "AAPL"),
            Arg.Any<CancellationToken>())
            .Returns(Result<PriceHistoryResponse>.Success(expectedResponse));

        await using WebApplicationFactory<Program> factory = CreateTestFactory(marketService);
        using HttpClient client = factory.CreateClient();

        string correlationId = Guid.NewGuid().ToString("D");
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/market/AAPL/history?interval=1day&limit=50");
        request.Headers.Add(CorrelationId.HeaderName, correlationId);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(CorrelationId.HeaderName, out IEnumerable<string>? headerValues).Should().BeTrue();
        headerValues!.First().Should().Be(correlationId);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(content);
        JsonElement root = doc.RootElement;

        root.GetProperty("symbol").GetString().Should().Be("AAPL");
        root.GetProperty("interval").GetString().Should().Be("1day");
        root.GetProperty("data").GetArrayLength().Should().Be(1);
        root.GetProperty("data")[0].GetProperty("close").GetDecimal().Should().Be(186.80m);
        root.GetProperty("pagination").GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetHistory_WhenFromGreaterThanTo_Returns400Rfc7807ProblemDetails()
    {
        IMarketDataService marketService = Substitute.For<IMarketDataService>();
        marketService.GetPriceHistoryAsync(
            Arg.Any<PriceHistoryQuery>(),
            Arg.Any<CancellationToken>())
            .Returns(Result<PriceHistoryResponse>.Failure(
                new Error("VALIDATION_FAILED", "The 'from' timestamp must be less than or equal to the 'to' timestamp.")));

        await using WebApplicationFactory<Program> factory = CreateTestFactory(marketService);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/market/AAPL/history?from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        root.GetProperty("type").GetString().Should().Be("https://finsightiq.io/errors/validation-failed");
        root.GetProperty("title").GetString().Should().Be("Validation failed");
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        root.GetProperty("instance").GetString().Should().Be("/api/v1/market/AAPL/history");
        root.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetLiveQuote_Returns200Ok_AndReturns404ForUnknownSymbol()
    {
        IMarketDataService marketService = Substitute.For<IMarketDataService>();
        DateTimeOffset asOf = new DateTimeOffset(2026, 1, 15, 14, 32, 10, TimeSpan.Zero);
        LiveQuoteDto quote = new LiveQuoteDto("AAPL", 189.30m, 2.35m, 1.26m, 54230100L, asOf, true);

        marketService.GetLiveQuoteAsync("AAPL", Arg.Any<CancellationToken>())
            .Returns(Result<LiveQuoteDto>.Success(quote));

        marketService.GetLiveQuoteAsync("ZZZZ", Arg.Any<CancellationToken>())
            .Returns(Result<LiveQuoteDto>.Failure(new Error("SYMBOL_NOT_FOUND", "Symbol 'ZZZZ' was not found.")));

        await using WebApplicationFactory<Program> factory = CreateTestFactory(marketService);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage okResponse = await client.GetAsync("/api/v1/market/AAPL/live");
        okResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string okBody = await okResponse.Content.ReadAsStringAsync();
        using JsonDocument okDoc = JsonDocument.Parse(okBody);
        okDoc.RootElement.GetProperty("symbol").GetString().Should().Be("AAPL");
        okDoc.RootElement.GetProperty("price").GetDecimal().Should().Be(189.30m);
        okDoc.RootElement.GetProperty("cached").GetBoolean().Should().BeTrue();

        using HttpResponseMessage notFoundResponse = await client.GetAsync("/api/v1/market/ZZZZ/live");
        notFoundResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string nfBody = await notFoundResponse.Content.ReadAsStringAsync();
        using JsonDocument nfDoc = JsonDocument.Parse(nfBody);
        nfDoc.RootElement.GetProperty("code").GetString().Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public async Task SearchSymbols_AndGetSymbols_ReturnExpectedPayloads()
    {
        IMarketDataService marketService = Substitute.For<IMarketDataService>();
        marketService.SearchSymbolsAsync("App", Arg.Any<CancellationToken>())
            .Returns(Result<SymbolSearchResponse>.Success(
                new SymbolSearchResponse(new[]
                {
                    new SymbolSearchResultDto("AAPL", "Apple Inc.", "NASDAQ", "Technology")
                })));

        marketService.GetSymbolsAsync(Arg.Any<SymbolsFilterQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<SymbolsListResponse>.Success(
                new SymbolsListResponse(
                    new[]
                    {
                        new SymbolDto { Symbol = "AAPL", Name = "Apple Inc.", Exchange = "NASDAQ", Sector = "Technology", Currency = "USD", IsActive = true }
                    },
                    new PaginationMeta(null, false, 50))));

        await using WebApplicationFactory<Program> factory = CreateTestFactory(marketService);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage searchResp = await client.GetAsync("/api/v1/market/search?q=App");
        searchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string searchJson = await searchResp.Content.ReadAsStringAsync();
        using JsonDocument searchDoc = JsonDocument.Parse(searchJson);
        searchDoc.RootElement.GetProperty("results")[0].GetProperty("symbol").GetString().Should().Be("AAPL");

        using HttpResponseMessage symbolsResp = await client.GetAsync("/api/v1/market/symbols?exchange=NASDAQ");
        symbolsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string symbolsJson = await symbolsResp.Content.ReadAsStringAsync();
        using JsonDocument symbolsDoc = JsonDocument.Parse(symbolsJson);
        symbolsDoc.RootElement.GetProperty("data")[0].GetProperty("symbol").GetString().Should().Be("AAPL");
    }

    [Fact]
    public async Task ExceptionHandlingMiddleware_Returns500Rfc7807_WithoutLeakingStackTrace()
    {
        IMarketDataService marketService = Substitute.For<IMarketDataService>();
        marketService.GetLiveQuoteAsync("BOOM", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database connection pool exhausted"));

        await using WebApplicationFactory<Program> factory = CreateTestFactory(marketService);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/market/BOOM/live");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("at FinSightIQ");
        body.Should().NotContain("StackTrace");

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().Should().Be("INTERNAL_ERROR");
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(500);
    }

    [Fact]
    public async Task HealthChecks_LiveAndReady_ReturnExpectedStatusCodes()
    {
        IMarketDataService marketService = Substitute.For<IMarketDataService>();
        IDapperWrapper healthyDapper = Substitute.For<IDapperWrapper>();
        healthyDapper.QuerySingleOrDefaultAsync<int>(
            "SELECT 1;",
            Arg.Any<object?>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<int?>(),
            Arg.Any<CommandType?>(),
            Arg.Any<CancellationToken>())
            .Returns(1);

        await using WebApplicationFactory<Program> healthyFactory = CreateTestFactory(marketService, healthyDapper);
        using HttpClient healthyClient = healthyFactory.CreateClient();

        using HttpResponseMessage liveResp = await healthyClient.GetAsync("/health/live");
        liveResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage readyResp = await healthyClient.GetAsync("/health/ready");
        readyResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Unhealthy PostgreSQL -> /health/ready returns 503 while /health/live still returns 200 (TC-SYS-05)
        IDapperWrapper failingDapper = Substitute.For<IDapperWrapper>();
        failingDapper.QuerySingleOrDefaultAsync<int>(
            "SELECT 1;",
            Arg.Any<object?>(),
            Arg.Any<IDbTransaction?>(),
            Arg.Any<int?>(),
            Arg.Any<CommandType?>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Postgres unreachable"));

        await using WebApplicationFactory<Program> unhealthyFactory = CreateTestFactory(marketService, failingDapper);
        using HttpClient unhealthyClient = unhealthyFactory.CreateClient();

        using HttpResponseMessage liveStillUp = await unhealthyClient.GetAsync("/health/live");
        liveStillUp.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage readyDown = await unhealthyClient.GetAsync("/health/ready");
        readyDown.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private static WebApplicationFactory<Program> CreateTestFactory(
        IMarketDataService marketDataService,
        IDapperWrapper? dapperWrapper = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.Configure<MarketDataOptions>(opts => opts.PollingEnabled = false);

                services.RemoveAll<IMarketDataService>();
                services.AddSingleton(marketDataService);

                services.RemoveAll<IDistributedCache>();
                services.AddDistributedMemoryCache();

                if (dapperWrapper is not null)
                {
                    services.RemoveAll<IDapperWrapper>();
                    services.AddSingleton(dapperWrapper);
                }
            });
        });
    }
}

