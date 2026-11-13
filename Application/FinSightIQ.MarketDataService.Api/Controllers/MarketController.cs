using FinSightIQ.BuildingBlocks.Results;
using FinSightIQ.MarketDataService.Api.Extensions;
using FinSightIQ.MarketDataService.Core.DTOs;
using FinSightIQ.MarketDataService.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FinSightIQ.MarketDataService.Api.Controllers;

/// <summary>
/// Public REST controller for market OHLCV price history, live cached quotes,
/// symbol search, and symbol universe listing.
/// </summary>
[ApiController]
[Route("api/v1/market")]
public sealed class MarketController : ControllerBase
{
    private readonly IMarketDataService _marketDataService;

    public MarketController(IMarketDataService marketDataService)
    {
        ArgumentNullException.ThrowIfNull(marketDataService);
        _marketDataService = marketDataService;
    }

    /// <summary>
    /// Retrieves cursor-paginated historical OHLCV bars for a tracked symbol.
    /// </summary>
    /// <param name="symbol">Ticker symbol (e.g. AAPL, MSFT).</param>
    /// <param name="from">Optional UTC start timestamp (inclusive).</param>
    /// <param name="to">Optional UTC end timestamp (inclusive).</param>
    /// <param name="interval">Bar interval (default '1day', or '1min', '5min', '15min', '30min', '60min').</param>
    /// <param name="limit">Page size between 1 and 500 (default 50).</param>
    /// <param name="cursor">Opaque pagination cursor returned by a previous page.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("{symbol}/history")]
    [ProducesResponseType(typeof(PriceHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(
        [FromRoute] string symbol,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string interval = "1day",
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        PriceHistoryQuery query = new PriceHistoryQuery(
            symbol,
            from,
            to,
            interval,
            limit,
            cursor);

        Result<PriceHistoryResponse> result = await _marketDataService.GetPriceHistoryAsync(query, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblemResult(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Retrieves the latest live stock quote for a symbol using Redis cache-aside (60s TTL)
    /// with fallback to PostgreSQL and upstream providers.
    /// </summary>
    /// <param name="symbol">Ticker symbol (e.g. AAPL, MSFT).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("{symbol}/live")]
    [HttpGet("{symbol}/quote")]
    [ProducesResponseType(typeof(LiveQuoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetLiveQuote(
        [FromRoute] string symbol,
        CancellationToken cancellationToken = default)
    {
        Result<LiveQuoteDto> result = await _marketDataService.GetLiveQuoteAsync(symbol, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblemResult(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Searches active symbols by ticker prefix or company name trigram similarity.
    /// </summary>
    /// <param name="q">Search query string (minimum 2 characters).</param>
    /// <param name="query">Optional alias for the search query string.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("search")]
    [ProducesResponseType(typeof(SymbolSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchSymbols(
        [FromQuery(Name = "q")] string? q = null,
        [FromQuery(Name = "query")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        string? effectiveQuery = !string.IsNullOrWhiteSpace(q) ? q : query;

        Result<SymbolSearchResponse> result = await _marketDataService.SearchSymbolsAsync(effectiveQuery, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblemResult(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Lists tracked symbols in the market universe with optional filtering and cursor pagination.
    /// </summary>
    /// <param name="exchange">Optional exchange filter (e.g. NASDAQ, NYSE).</param>
    /// <param name="sector">Optional sector filter (e.g. Technology, Financials).</param>
    /// <param name="active">Optional active status filter.</param>
    /// <param name="limit">Page size between 1 and 500 (default 50).</param>
    /// <param name="cursor">Opaque pagination cursor returned by a previous page.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("symbols")]
    [ProducesResponseType(typeof(SymbolsListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetSymbols(
        [FromQuery] string? exchange = null,
        [FromQuery] string? sector = null,
        [FromQuery] bool? active = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        SymbolsFilterQuery filterQuery = new SymbolsFilterQuery(
            exchange,
            sector,
            active,
            limit,
            cursor);

        Result<SymbolsListResponse> result = await _marketDataService.GetSymbolsAsync(filterQuery, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblemResult(result.Error);
        }

        return Ok(result.Value);
    }
}
