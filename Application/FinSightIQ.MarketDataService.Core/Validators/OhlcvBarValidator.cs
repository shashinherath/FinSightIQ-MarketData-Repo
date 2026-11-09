using FinSightIQ.MarketDataService.Core.DTOs;
using FluentValidation;

namespace FinSightIQ.MarketDataService.Core.Validators;

/// <summary>
/// Validates OHLCV bars against domain invariants and database check constraints
/// (high >= low, high >= open, high >= close, low <= open, low <= close, volume >= 0).
/// </summary>
public sealed class OhlcvBarValidator : AbstractValidator<OhlcvBarDto>
{
    public OhlcvBarValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .MaximumLength(10);

        RuleFor(x => x.Open)
            .GreaterThan(0m);

        RuleFor(x => x.High)
            .GreaterThan(0m);

        RuleFor(x => x.Low)
            .GreaterThan(0m);

        RuleFor(x => x.Close)
            .GreaterThan(0m);

        RuleFor(x => x.Volume)
            .GreaterThanOrEqualTo(0L);

        RuleFor(x => x.High)
            .GreaterThanOrEqualTo(x => x.Low)
            .WithMessage("High must be greater than or equal to Low (ck_prices_hl).");

        RuleFor(x => x)
            .Must(bar => bar.High >= bar.Open && bar.High >= bar.Close && bar.Low <= bar.Open && bar.Low <= bar.Close)
            .WithMessage("High/Low band must enclose both Open and Close (ck_prices_band).");

        RuleFor(x => x.Interval)
            .NotEmpty()
            .MaximumLength(10);

        RuleFor(x => x.Source)
            .NotEmpty()
            .MaximumLength(20);
    }

    /// <summary>
    /// Fast predicate check for filtering upstream bars before database persistence.
    /// </summary>
    public static bool IsValid(OhlcvBarDto? bar, out string? failureReason)
    {
        if (bar is null)
        {
            failureReason = "Bar is null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bar.Symbol) || bar.Symbol.Trim().Length > 10)
        {
            failureReason = "Symbol is empty or exceeds 10 characters.";
            return false;
        }

        if (bar.Open <= 0m || bar.High <= 0m || bar.Low <= 0m || bar.Close <= 0m)
        {
            failureReason = "OHLC prices must all be strictly positive.";
            return false;
        }

        if (bar.High < bar.Low)
        {
            failureReason = $"High ({bar.High}) is less than Low ({bar.Low}).";
            return false;
        }

        if (bar.High < bar.Open || bar.High < bar.Close || bar.Low > bar.Open || bar.Low > bar.Close)
        {
            failureReason = $"OHLC band invariant violated: Open={bar.Open}, High={bar.High}, Low={bar.Low}, Close={bar.Close}.";
            return false;
        }

        if (bar.Volume < 0L)
        {
            failureReason = $"Volume ({bar.Volume}) cannot be negative.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bar.Interval) || bar.Interval.Trim().Length > 10)
        {
            failureReason = "Interval is empty or exceeds 10 characters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(bar.Source) || bar.Source.Trim().Length > 20)
        {
            failureReason = "Source is empty or exceeds 20 characters.";
            return false;
        }

        failureReason = null;
        return true;
    }
}

