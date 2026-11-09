using FinSightIQ.MarketDataService.Core.Interfaces;

namespace FinSightIQ.MarketDataService.Core.Services;

/// <summary>
/// DST-aware NYSE/NASDAQ market hours and holiday guard (09:30–16:00 America/New_York, Mon–Fri).
/// </summary>
public sealed class NyseMarketHoursGuard : IMarketHoursGuard
{
    private static readonly TimeZoneInfo EasternTimeZone = ResolveEasternTimeZone();
    private static readonly TimeSpan MarketOpenTime = new TimeSpan(9, 30, 0);
    private static readonly TimeSpan MarketCloseTime = new TimeSpan(16, 0, 0);

    public bool IsMarketOpen(DateTimeOffset utcTimestamp)
    {
        DateTimeOffset easternTime = TimeZoneInfo.ConvertTime(utcTimestamp.ToUniversalTime(), EasternTimeZone);

        if (easternTime.DayOfWeek == DayOfWeek.Saturday || easternTime.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        DateOnly easternDate = DateOnly.FromDateTime(easternTime.DateTime);
        if (IsNyseHoliday(easternDate))
        {
            return false;
        }

        TimeSpan timeOfDay = easternTime.TimeOfDay;
        return timeOfDay >= MarketOpenTime && timeOfDay <= MarketCloseTime;
    }

    public bool IsNyseHoliday(DateOnly easternDate)
    {
        if (easternDate.DayOfWeek == DayOfWeek.Saturday || easternDate.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        int year = easternDate.Year;

        // 1. New Year's Day (Jan 1 of current year, or Dec 31 if next year's Jan 1 falls on Saturday)
        if (easternDate == ObserveFixedHoliday(new DateOnly(year, 1, 1)) ||
            easternDate == ObserveFixedHoliday(new DateOnly(year + 1, 1, 1)))
        {
            return true;
        }

        // 2. Martin Luther King, Jr. Day (3rd Monday in January)
        if (easternDate == GetNthDayOfWeek(year, 1, DayOfWeek.Monday, 3))
        {
            return true;
        }

        // 3. Washington's Birthday / Presidents' Day (3rd Monday in February)
        if (easternDate == GetNthDayOfWeek(year, 2, DayOfWeek.Monday, 3))
        {
            return true;
        }

        // 4. Good Friday (2 days before Easter Sunday)
        DateOnly easterSunday = CalculateEasterSunday(year);
        if (easternDate == easterSunday.AddDays(-2))
        {
            return true;
        }

        // 5. Memorial Day (Last Monday in May)
        if (easternDate == GetLastDayOfWeek(year, 5, DayOfWeek.Monday))
        {
            return true;
        }

        // 6. Juneteenth National Independence Day (June 19, observed since 2022)
        if (year >= 2022 && easternDate == ObserveFixedHoliday(new DateOnly(year, 6, 19)))
        {
            return true;
        }

        // 7. Independence Day (July 4)
        if (easternDate == ObserveFixedHoliday(new DateOnly(year, 7, 4)))
        {
            return true;
        }

        // 8. Labor Day (1st Monday in September)
        if (easternDate == GetNthDayOfWeek(year, 9, DayOfWeek.Monday, 1))
        {
            return true;
        }

        // 9. Thanksgiving Day (4th Thursday in November)
        if (easternDate == GetNthDayOfWeek(year, 11, DayOfWeek.Thursday, 4))
        {
            return true;
        }

        // 10. Christmas Day (December 25)
        if (easternDate == ObserveFixedHoliday(new DateOnly(year, 12, 25)))
        {
            return true;
        }

        return false;
    }

    private static DateOnly ObserveFixedHoliday(DateOnly actualDate)
    {
        return actualDate.DayOfWeek switch
        {
            DayOfWeek.Saturday => actualDate.AddDays(-1),
            DayOfWeek.Sunday => actualDate.AddDays(1),
            _ => actualDate
        };
    }

    private static DateOnly GetNthDayOfWeek(int year, int month, DayOfWeek dayOfWeek, int occurrence)
    {
        DateOnly firstOfMonth = new DateOnly(year, month, 1);
        int daysOffset = ((int)dayOfWeek - (int)firstOfMonth.DayOfWeek + 7) % 7;
        return firstOfMonth.AddDays(daysOffset + ((occurrence - 1) * 7));
    }

    private static DateOnly GetLastDayOfWeek(int year, int month, DayOfWeek dayOfWeek)
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);
        DateOnly lastOfMonth = new DateOnly(year, month, daysInMonth);
        int daysBack = ((int)lastOfMonth.DayOfWeek - (int)dayOfWeek + 7) % 7;
        return lastOfMonth.AddDays(-daysBack);
    }

    /// <summary>
    /// Computes Easter Sunday for a given Gregorian year using the Butcher-Meeus algorithm.
    /// </summary>
    private static DateOnly CalculateEasterSunday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31;
        int day = ((h + l - (7 * m) + 114) % 31) + 1;

        return new DateOnly(year, month, day);
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

