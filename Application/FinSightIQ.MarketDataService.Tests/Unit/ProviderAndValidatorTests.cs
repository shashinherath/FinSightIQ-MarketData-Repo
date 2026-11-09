using System.Net;
using System.Text;
using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Validators;
using FinSightIQ.MarketDataService.Infrastructure.ExternalApis;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FinSightIQ.MarketDataService.Tests.Unit;

public sealed class ProviderAndValidatorTests
{
    [Fact]
    public void OhlcvBarValidator_RejectsMalformedBars_WhenHighLessThanLowOrNegativeVolume()
    {
        OhlcvBarDto highLessThanLow = new OhlcvBarDto(
            "AAPL",
            185.0m,
            180.0m,
            182.0m,
            181.0m,
            1000L,
            "1day",
            MarketDataProviderNames.AlphaVantage,
            DateTimeOffset.UtcNow);

        bool isValid1 = OhlcvBarValidator.IsValid(highLessThanLow, out string? reason1);
        isValid1.Should().BeFalse();
        reason1.Should().Contain("High");

        OhlcvBarDto negativeVolume = new OhlcvBarDto(
            "AAPL",
            185.0m,
            190.0m,
            182.0m,
            188.0m,
            -50L,
            "1day",
            MarketDataProviderNames.AlphaVantage,
            DateTimeOffset.UtcNow);

        bool isValid2 = OhlcvBarValidator.IsValid(negativeVolume, out string? reason2);
        isValid2.Should().BeFalse();
        reason2.Should().Contain("Volume");
    }

    [Fact]
    public async Task AlphaVantageProvider_ParsesValidBars_AndRejectsMalformedBars()
    {
        const string jsonResponse = """
        {
          "Meta Data": {
            "1. Information": "Daily Prices",
            "2. Symbol": "AAPL"
          },
          "Time Series (Daily)": {
            "2026-01-15": {
              "1. open": "185.1000",
              "2. high": "187.4500",
              "3. low": "184.2000",
              "4. close": "186.8000",
              "5. volume": "54230100"
            },
            "2026-01-14": {
              "1. open": "185.0000",
              "2. high": "180.0000",
              "3. low": "184.0000",
              "4. close": "182.0000",
              "5. volume": "10000"
            }
          }
        }
        """;

        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            });

        HttpClient httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.alphavantage.co/")
        };

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            AlphaVantage = new AlphaVantageOptions
            {
                ApiKey = "test-api-key"
            }
        });

        IConfiguration configuration = new ConfigurationBuilder().Build();
        AlphaVantageProvider provider = new AlphaVantageProvider(
            httpClient,
            options,
            configuration,
            NullLogger<AlphaVantageProvider>.Instance);

        Result<IReadOnlyList<OhlcvBarDto>> result = await provider.GetBarsAsync("AAPL", "1day");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Symbol.Should().Be("AAPL");
        result.Value[0].Open.Should().Be(185.1000m);
        result.Value[0].High.Should().Be(187.4500m);
        result.Value[0].Low.Should().Be(184.2000m);
        result.Value[0].Close.Should().Be(186.8000m);
        result.Value[0].Volume.Should().Be(54230100L);
        result.Value[0].Source.Should().Be(MarketDataProviderNames.AlphaVantage);
    }

    [Fact]
    public async Task YahooFinanceProvider_ParsesValidChartPayload()
    {
        const string jsonResponse = """
        {
          "chart": {
            "result": [
              {
                "meta": {
                  "symbol": "MSFT"
                },
                "timestamp": [ 1768435200 ],
                "indicators": {
                  "quote": [
                    {
                      "open": [ 410.25 ],
                      "high": [ 415.00 ],
                      "low": [ 409.50 ],
                      "close": [ 414.10 ],
                      "volume": [ 22100500 ]
                    }
                  ]
                }
              }
            ],
            "error": null
          }
        }
        """;

        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            });

        HttpClient httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://query1.finance.yahoo.com/")
        };

        YahooFinanceProvider provider = new YahooFinanceProvider(
            httpClient,
            NullLogger<YahooFinanceProvider>.Instance);

        Result<IReadOnlyList<OhlcvBarDto>> result = await provider.GetBarsAsync("MSFT", "1day");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Symbol.Should().Be("MSFT");
        result.Value[0].Close.Should().Be(414.10m);
        result.Value[0].Volume.Should().Be(22100500L);
        result.Value[0].Source.Should().Be(MarketDataProviderNames.YahooFinance);
    }

    [Fact]
    public async Task MarketDataProviderOrchestrator_FallsBackToYahooFinance_WhenAlphaVantageIsRateLimited()
    {
        IMarketDataProvider primary = Substitute.For<IMarketDataProvider>();
        primary.ProviderName.Returns(MarketDataProviderNames.AlphaVantage);
        primary.GetBarsAsync("AAPL", "1min", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("UPSTREAM_RATE_LIMITED", "HTTP 429 Too Many Requests")));

        OhlcvBarDto fallbackBar = new OhlcvBarDto(
            "AAPL",
            188.0m,
            189.5m,
            187.5m,
            189.25m,
            300000L,
            "1min",
            MarketDataProviderNames.YahooFinance,
            DateTimeOffset.UtcNow);

        IMarketDataProvider secondary = Substitute.For<IMarketDataProvider>();
        secondary.ProviderName.Returns(MarketDataProviderNames.YahooFinance);
        secondary.GetBarsAsync("AAPL", "1min", Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<OhlcvBarDto>>.Success(new[] { fallbackBar }));

        IProviderHealthRepository healthRepo = Substitute.For<IProviderHealthRepository>();
        healthRepo.GetByProviderAsync(MarketDataProviderNames.AlphaVantage, Arg.Any<CancellationToken>())
            .Returns(new ProviderHealthDto
            {
                Provider = MarketDataProviderNames.AlphaVantage,
                ConsecutiveFailures = 4,
                CircuitState = ProviderCircuitState.Closed
            });

        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerBreakSeconds = 30
        });

        MarketDataProviderOrchestrator orchestrator = new MarketDataProviderOrchestrator(
            new[] { primary, secondary },
            healthRepo,
            options,
            NullLogger<MarketDataProviderOrchestrator>.Instance);

        Result<IReadOnlyList<OhlcvBarDto>> result = await orchestrator.FetchBarsWithFallbackAsync("AAPL", "1min");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Source.Should().Be(MarketDataProviderNames.YahooFinance);

        await healthRepo.Received(1).RecordFailureAsync(
            MarketDataProviderNames.AlphaVantage,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<string>(),
            ProviderCircuitState.Open,
            Arg.Any<CancellationToken>());

        await healthRepo.Received(1).RecordSuccessAsync(
            MarketDataProviderNames.YahooFinance,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
