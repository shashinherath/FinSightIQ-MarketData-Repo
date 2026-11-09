using System.Net;
using System.Text.Json;
using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Validators;
using Microsoft.Extensions.Logging;

namespace FinSightIQ.MarketDataService.Infrastructure.ExternalApis;

/// <summary>
/// Secondary/fallback market data provider integrating with Yahoo Finance chart API.
/// </summary>
public sealed class YahooFinanceProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<YahooFinanceProvider> _logger;

    public YahooFinanceProvider(
        HttpClient httpClient,
        ILogger<YahooFinanceProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
    }

    public string ProviderName => MarketDataProviderNames.YahooFinance;

    public async Task<Result<IReadOnlyList<OhlcvBarDto>>> GetBarsAsync(
        string symbol,
        string interval = "1day",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("VALIDATION_FAILED", "Symbol is required."));
        }

        string normalizedSymbol = symbol.Trim().ToUpperInvariant();
        string normalizedInterval = NormalizeInterval(interval);
        string requestUri = BuildRequestUri(normalizedSymbol, normalizedInterval);

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "Yahoo Finance returned HTTP 429 rate limit for {Symbol} ({Interval}).",
                    normalizedSymbol,
                    normalizedInterval);

                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_RATE_LIMITED", "Yahoo Finance rate limit exceeded (HTTP 429)."));
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Yahoo Finance returned non-success status {StatusCode} for {Symbol} ({Interval}).",
                    (int)response.StatusCode,
                    normalizedSymbol,
                    normalizedInterval);

                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_UNAVAILABLE", $"Yahoo Finance returned HTTP {(int)response.StatusCode}."));
            }

            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_UNAVAILABLE", "Yahoo Finance returned an empty payload."));
            }

            return ParseResponse(payload, normalizedSymbol, normalizedInterval);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Yahoo Finance request failed for {Symbol} ({Interval}).",
                normalizedSymbol,
                normalizedInterval);

            return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("UPSTREAM_UNAVAILABLE", $"Yahoo Finance request failed: {ex.Message}"));
        }
    }

    private Result<IReadOnlyList<OhlcvBarDto>> ParseResponse(
        string jsonPayload,
        string symbol,
        string interval)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonPayload);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("chart", out JsonElement chartElement) ||
                chartElement.ValueKind != JsonValueKind.Object)
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_MALFORMED_RESPONSE", "Yahoo Finance payload is missing 'chart' object."));
            }

            if (chartElement.TryGetProperty("error", out JsonElement errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object)
            {
                string description = errorElement.TryGetProperty("description", out JsonElement descProp)
                    ? descProp.GetString() ?? "Yahoo Finance chart error."
                    : "Yahoo Finance chart error.";

                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_ERROR", description));
            }

            if (!chartElement.TryGetProperty("result", out JsonElement resultArray) ||
                resultArray.ValueKind != JsonValueKind.Array ||
                resultArray.GetArrayLength() == 0)
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_EMPTY_DATA", $"Yahoo Finance returned empty result array for '{symbol}'."));
            }

            JsonElement firstResult = resultArray[0];
            List<OhlcvBarDto> validBars = new List<OhlcvBarDto>();

            if (firstResult.TryGetProperty("timestamp", out JsonElement timestampsElement) &&
                timestampsElement.ValueKind == JsonValueKind.Array &&
                firstResult.TryGetProperty("indicators", out JsonElement indicatorsElement) &&
                indicatorsElement.TryGetProperty("quote", out JsonElement quoteArray) &&
                quoteArray.ValueKind == JsonValueKind.Array &&
                quoteArray.GetArrayLength() > 0)
            {
                JsonElement quote = quoteArray[0];
                bool hasOpen = quote.TryGetProperty("open", out JsonElement openArray) && openArray.ValueKind == JsonValueKind.Array;
                bool hasHigh = quote.TryGetProperty("high", out JsonElement highArray) && highArray.ValueKind == JsonValueKind.Array;
                bool hasLow = quote.TryGetProperty("low", out JsonElement lowArray) && lowArray.ValueKind == JsonValueKind.Array;
                bool hasClose = quote.TryGetProperty("close", out JsonElement closeArray) && closeArray.ValueKind == JsonValueKind.Array;
                bool hasVolume = quote.TryGetProperty("volume", out JsonElement volumeArray) && volumeArray.ValueKind == JsonValueKind.Array;

                if (hasOpen && hasHigh && hasLow && hasClose && hasVolume)
                {
                    int count = new[]
                    {
                        timestampsElement.GetArrayLength(),
                        openArray.GetArrayLength(),
                        highArray.GetArrayLength(),
                        lowArray.GetArrayLength(),
                        closeArray.GetArrayLength(),
                        volumeArray.GetArrayLength()
                    }.Min();

                    for (int i = 0; i < count; i++)
                    {
                        if (!TryReadInt64(timestampsElement[i], out long unixSeconds) ||
                            !TryReadDecimal(openArray[i], out decimal open) ||
                            !TryReadDecimal(highArray[i], out decimal high) ||
                            !TryReadDecimal(lowArray[i], out decimal low) ||
                            !TryReadDecimal(closeArray[i], out decimal close) ||
                            !TryReadInt64(volumeArray[i], out long volume))
                        {
                            continue;
                        }

                        DateTimeOffset recordedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                        if (string.Equals(interval, "1day", StringComparison.OrdinalIgnoreCase))
                        {
                            recordedAt = new DateTimeOffset(
                                recordedAt.UtcDateTime.Year,
                                recordedAt.UtcDateTime.Month,
                                recordedAt.UtcDateTime.Day,
                                0,
                                0,
                                0,
                                TimeSpan.Zero);
                        }

                        OhlcvBarDto candidate = new OhlcvBarDto(
                            symbol,
                            decimal.Round(open, 4),
                            decimal.Round(high, 4),
                            decimal.Round(low, 4),
                            decimal.Round(close, 4),
                            volume,
                            interval,
                            ProviderName,
                            recordedAt);

                        if (!OhlcvBarValidator.IsValid(candidate, out string? failureReason))
                        {
                            _logger.LogWarning(
                                "Rejected malformed Yahoo Finance bar for {Symbol} at {RecordedAt}: {Reason}",
                                symbol,
                                recordedAt,
                                failureReason);
                            continue;
                        }

                        validBars.Add(candidate);
                    }
                }
            }

            if (validBars.Count == 0 &&
                firstResult.TryGetProperty("meta", out JsonElement metaElement) &&
                metaElement.ValueKind == JsonValueKind.Object &&
                TryParseMetaFallback(metaElement, symbol, interval, out OhlcvBarDto? metaBar) &&
                metaBar is not null)
            {
                if (OhlcvBarValidator.IsValid(metaBar, out string? failureReason))
                {
                    validBars.Add(metaBar);
                }
                else
                {
                    _logger.LogWarning(
                        "Rejected malformed Yahoo Finance meta fallback bar for {Symbol}: {Reason}",
                        symbol,
                        failureReason);
                }
            }

            if (validBars.Count == 0)
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_EMPTY_DATA", $"Yahoo Finance returned no valid OHLCV bars for '{symbol}'."));
            }

            List<OhlcvBarDto> orderedBars = validBars
                .OrderByDescending(b => b.RecordedAt)
                .ToList();

            return Result<IReadOnlyList<OhlcvBarDto>>.Success(orderedBars);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Malformed JSON received from Yahoo Finance for {Symbol}.",
                symbol);

            return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("UPSTREAM_MALFORMED_RESPONSE", $"Invalid JSON from Yahoo Finance: {ex.Message}"));
        }
    }

    private bool TryParseMetaFallback(
        JsonElement meta,
        string symbol,
        string interval,
        out OhlcvBarDto? bar)
    {
        bar = null;
        if (!meta.TryGetProperty("regularMarketPrice", out JsonElement priceProp) ||
            !TryReadDecimal(priceProp, out decimal close))
        {
            return false;
        }

        decimal open = close;
        if (meta.TryGetProperty("chartPreviousClose", out JsonElement prevCloseProp) &&
            TryReadDecimal(prevCloseProp, out decimal prevClose) &&
            prevClose > 0m)
        {
            open = prevClose;
        }

        decimal high = Math.Max(open, close);
        if (meta.TryGetProperty("regularMarketDayHigh", out JsonElement highProp) &&
            TryReadDecimal(highProp, out decimal dayHigh) &&
            dayHigh >= high)
        {
            high = dayHigh;
        }

        decimal low = Math.Min(open, close);
        if (meta.TryGetProperty("regularMarketDayLow", out JsonElement lowProp) &&
            TryReadDecimal(lowProp, out decimal dayLow) &&
            dayLow > 0m &&
            dayLow <= low)
        {
            low = dayLow;
        }

        long volume = 0L;
        if (meta.TryGetProperty("regularMarketVolume", out JsonElement volProp) &&
            TryReadInt64(volProp, out long dayVol))
        {
            volume = dayVol;
        }

        DateTimeOffset recordedAt = DateTimeOffset.UtcNow;
        if (meta.TryGetProperty("regularMarketTime", out JsonElement timeProp) &&
            TryReadInt64(timeProp, out long unixSeconds))
        {
            recordedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        bar = new OhlcvBarDto(
            symbol,
            decimal.Round(open, 4),
            decimal.Round(high, 4),
            decimal.Round(low, 4),
            decimal.Round(close, 4),
            volume,
            interval,
            ProviderName,
            recordedAt);

        return true;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = 0m;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            value = (decimal)d;
            return true;
        }

        return false;
    }

    private static bool TryReadInt64(JsonElement element, out long value)
    {
        value = 0L;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static string BuildRequestUri(string symbol, string interval)
    {
        string escapedSymbol = Uri.EscapeDataString(symbol);
        if (string.Equals(interval, "1day", StringComparison.OrdinalIgnoreCase))
        {
            return $"v8/finance/chart/{escapedSymbol}?interval=1d&range=1mo";
        }

        string yahooInterval = interval switch
        {
            "1min" => "1m",
            "5min" => "5m",
            "15min" => "15m",
            "30min" => "30m",
            "60min" => "60m",
            _ => "1m"
        };

        return $"v8/finance/chart/{escapedSymbol}?interval={yahooInterval}&range=1d";
    }

    private static string NormalizeInterval(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval))
        {
            return "1day";
        }

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
}
