namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Evaluates whether the US equity market (NYSE/NASDAQ) is open at a given UTC timestamp.
/// </summary>
public interface IMarketHoursGuard
{
    bool IsMarketOpen(DateTimeOffset utcTimestamp);

    bool IsNyseHoliday(DateOnly easternDate);
}

