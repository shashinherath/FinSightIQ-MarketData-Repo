using System.Globalization;
using System.Net;
using System.Text.Json;
using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using FinSightIQ.MarketDataService.Core.Validators;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSightIQ.MarketDataService.Infrastructure.ExternalApis;

/// <summary>
/// Primary market data provider integrating with the Alpha Vantage REST API.
/// </summary>
public sealed class AlphaVantageProvider : IMarketDataProvider
{
    private static readonly TimeZoneInfo EasternTimeZone = ResolveEasternTimeZone();

    private readonly HttpClient _httpClient;
    private readonly AlphaVantageOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AlphaVantageProvider> _logger;

    public AlphaVantageProvider(
        HttpClient httpClient,
        IOptions<MarketDataOptions> options,
        IConfiguration configuration,
        ILogger<AlphaVantageProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value.AlphaVantage;
        _configuration = configuration;
        _logger = logger;
    }

    public string ProviderName => MarketDataProviderNames.AlphaVantage;

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
        string apiKey = ResolveApiKey();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "Alpha Vantage API key is not configured when requesting {Symbol} ({Interval}).",
                normalizedSymbol,
                normalizedInterval);

            return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("UPSTREAM_UNAVAILABLE", "Alpha Vantage API key is not configured."));
        }

        string requestUri = BuildRequestUri(normalizedSymbol, normalizedInterval, apiKey);

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "Alpha Vantage returned HTTP 429 rate limit for {Symbol} ({Interval}).",
                    normalizedSymbol,
                    normalizedInterval);

                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_RATE_LIMITED", "Alpha Vantage rate limit exceeded (HTTP 429)."));
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Alpha Vantage returned non-success status {StatusCode} for {Symbol} ({Interval}).",
                    (int)response.StatusCode,
                    normalizedSymbol,
                    normalizedInterval);

                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_UNAVAILABLE", $"Alpha Vantage returned HTTP {(int)response.StatusCode}."));
            }

            string payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_UNAVAILABLE", "Alpha Vantage returned an empty payload."));
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
                "Alpha Vantage request failed for {Symbol} ({Interval}).",
                normalizedSymbol,
                normalizedInterval);

            return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("UPSTREAM_UNAVAILABLE", $"Alpha Vantage request failed: {ex.Message}"));
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

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_MALFORMED_RESPONSE", "Alpha Vantage response root is not a JSON object."));
            }

            bool hasNote = root.TryGetProperty("Note", out JsonElement noteElement);
            bool hasInfo = root.TryGetProperty("Information", out JsonElement infoElement);
            if (hasNote || hasInfo)
            {
                string rateLimitMessage = hasNote
                    ? noteElement.GetString() ?? "Alpha Vantage rate limit note received."
                    : infoElement.GetString() ?? "Alpha Vantage information limit received.";

                _logger.LogWarning(
                    "Alpha Vantage rate-limit payload received for {Symbol}: {Message}",
                    symbol,
                    rateLimitMessage);

                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_RATE_LIMITED", rateLimitMessage));
            }

            if (root.TryGetProperty("Error Message", out JsonElement errorElement))
            {
                string errorMessage = errorElement.GetString() ?? "Alpha Vantage returned an error message.";
                _logger.LogWarning(
                    "Alpha Vantage error message for {Symbol}: {Message}",
                    symbol,
                    errorMessage);

                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_ERROR", errorMessage));
            }

            List<OhlcvBarDto> validBars = new List<OhlcvBarDto>();

            JsonElement? timeSeriesElement = FindTimeSeriesElement(root);
            if (timeSeriesElement.HasValue && timeSeriesElement.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in timeSeriesElement.Value.EnumerateObject())
                {
                    if (!TryParseTimestamp(entry.Name, interval, out DateTimeOffset recordedAtUtc))
                    {
                        _logger.LogWarning(
                            "Rejected Alpha Vantage bar with invalid timestamp '{Timestamp}' for {Symbol}.",
                            entry.Name,
                            symbol);
                        continue;
                    }

                    if (!TryParseBarValues(entry.Value, out decimal open, out decimal high, out decimal low, out decimal close, out long volume))
                    {
                        _logger.LogWarning(
                            "Rejected Alpha Vantage bar with non-numeric OHLCV fields for {Symbol} at {RecordedAt}.",
                            symbol,
                            recordedAtUtc);
                        continue;
                    }

                    OhlcvBarDto candidate = new OhlcvBarDto(
                        symbol,
                        open,
                        high,
                        low,
                        close,
                        volume,
                        interval,
                        ProviderName,
                        recordedAtUtc);

                    if (!OhlcvBarValidator.IsValid(candidate, out string? failureReason))
                    {
                        _logger.LogWarning(
                            "Rejected malformed Alpha Vantage bar for {Symbol} at {RecordedAt}: {Reason}",
                            symbol,
                            recordedAtUtc,
                            failureReason);
                        continue;
                    }

                    validBars.Add(candidate);
                }
            }
            else if (root.TryGetProperty("Global Quote", out JsonElement globalQuote) &&
                     globalQuote.ValueKind == JsonValueKind.Object)
            {
                if (TryParseGlobalQuote(globalQuote, symbol, interval, out OhlcvBarDto? quoteBar) && quoteBar is not null)
                {
                    if (OhlcvBarValidator.IsValid(quoteBar, out string? failureReason))
                    {
                        validBars.Add(quoteBar);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Rejected malformed Alpha Vantage Global Quote for {Symbol}: {Reason}",
                            symbol,
                            failureReason);
                    }
                }
            }
            else
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_MALFORMED_RESPONSE", "Alpha Vantage payload did not contain a recognized Time Series or Global Quote object."));
            }

            if (validBars.Count == 0)
            {
                return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                    new Error("UPSTREAM_EMPTY_DATA", $"Alpha Vantage returned no valid OHLCV bars for '{symbol}'."));
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
                "Malformed JSON received from Alpha Vantage for {Symbol}.",
                symbol);

            return Result<IReadOnlyList<OhlcvBarDto>>.Failure(
                new Error("UPSTREAM_MALFORMED_RESPONSE", $"Invalid JSON from Alpha Vantage: {ex.Message}"));
        }
    }

    private static JsonElement? FindTimeSeriesElement(JsonElement root)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name.StartsWith("Time Series", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static bool TryParseBarValues(
        JsonElement element,
        out decimal open,
        out decimal high,
        out decimal low,
        out decimal close,
        out long volume)
    {
        open = 0m;
        high = 0m;
        low = 0m;
        close = 0m;
        volume = 0L;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? openStr = GetPropertyString(element, "1. open", "open");
        string? highStr = GetPropertyString(element, "2. high", "high");
        string? lowStr = GetPropertyString(element, "3. low", "low");
        string? closeStr = GetPropertyString(element, "4. close", "close");
        string? volumeStr = GetPropertyString(element, "5. volume", "6. volume", "volume");

        return decimal.TryParse(openStr, NumberStyles.Number, CultureInfo.InvariantCulture, out open)
            && decimal.TryParse(highStr, NumberStyles.Number, CultureInfo.InvariantCulture, out high)
            && decimal.TryParse(lowStr, NumberStyles.Number, CultureInfo.InvariantCulture, out low)
            && decimal.TryParse(closeStr, NumberStyles.Number, CultureInfo.InvariantCulture, out close)
            && long.TryParse(volumeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out volume);
    }

    private bool TryParseGlobalQuote(
        JsonElement quoteElement,
        string symbol,
        string interval,
        out OhlcvBarDto? bar)
    {
        bar = null;
        string? openStr = GetPropertyString(quoteElement, "02. open");
        string? highStr = GetPropertyString(quoteElement, "03. high");
        string? lowStr = GetPropertyString(quoteElement, "04. low");
        string? priceStr = GetPropertyString(quoteElement, "05. price");
        string? volumeStr = GetPropertyString(quoteElement, "06. volume");
        string? tradingDayStr = GetPropertyString(quoteElement, "07. latest trading day");

        if (!decimal.TryParse(openStr, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal open) ||
            !decimal.TryParse(highStr, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal high) ||
            !decimal.TryParse(lowStr, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal low) ||
            !decimal.TryParse(priceStr, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal close) ||
            !long.TryParse(volumeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long volume))
        {
            return false;
        }

        DateTimeOffset recordedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(tradingDayStr) &&
            TryParseTimestamp(tradingDayStr, "1day", out DateTimeOffset parsedDay))
        {
            recordedAt = parsedDay;
        }

        bar = new OhlcvBarDto(
            symbol,
            open,
            high,
            low,
            close,
            volume,
            interval,
            ProviderName,
            recordedAt);

        return true;
    }

    private static string? GetPropertyString(JsonElement element, params string[] candidateNames)
    {
        foreach (string name in candidateNames)
        {
            if (element.TryGetProperty(name, out JsonElement prop))
            {
                return prop.ValueKind == JsonValueKind.String
                    ? prop.GetString()
                    : prop.GetRawText();
            }
        }

        return null;
    }

    private static bool TryParseTimestamp(string rawTimestamp, string interval, out DateTimeOffset recordedAtUtc)
    {
        recordedAtUtc = default;

        if (DateOnly.TryParseExact(rawTimestamp.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly dateOnly))
        {
            recordedAtUtc = new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero);
            return true;
        }

        if (DateTime.TryParseExact(
            rawTimestamp.Trim(),
            new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" },
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime easternWallTime))
        {
            DateTime unspecified = DateTime.SpecifyKind(easternWallTime, DateTimeKind.Unspecified);
            TimeSpan offset = EasternTimeZone.GetUtcOffset(unspecified);
            DateTimeOffset easternDto = new DateTimeOffset(unspecified, offset);
            recordedAtUtc = easternDto.ToUniversalTime();
            return true;
        }

        if (DateTimeOffset.TryParse(
            rawTimestamp.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset dto))
        {
            recordedAtUtc = dto.ToUniversalTime();
            return true;
        }

        return false;
    }

    private string BuildRequestUri(string symbol, string interval, string apiKey)
    {
        string escapedSymbol = Uri.EscapeDataString(symbol);
        string escapedKey = Uri.EscapeDataString(apiKey);

        if (string.Equals(interval, "1day", StringComparison.OrdinalIgnoreCase))
        {
            return $"query?function=TIME_SERIES_DAILY&symbol={escapedSymbol}&outputsize=compact&apikey={escapedKey}";
        }

        string avInterval = interval switch
        {
            "1min" => "1min",
            "5min" => "5min",
            "15min" => "15min",
            "30min" => "30min",
            "60min" => "60min",
            _ => "1min"
        };

        return $"query?function=TIME_SERIES_INTRADAY&symbol={escapedSymbol}&interval={avInterval}&outputsize=compact&apikey={escapedKey}";
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return _options.ApiKey;
        }

        string? configKey = _configuration["MarketData:AlphaVantage:ApiKey"]
            ?? _configuration["ALPHA_VANTAGE_API_KEY"];

        return configKey ?? string.Empty;
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

    private static TimeZoneInfo ResolveEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
