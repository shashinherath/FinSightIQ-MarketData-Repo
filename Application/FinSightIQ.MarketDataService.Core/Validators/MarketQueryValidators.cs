using FinSightIQ.MarketDataService.Core.DTOs;
using FluentValidation;

namespace FinSightIQ.MarketDataService.Core.Validators;

/// <summary>
/// Validates <see cref="PriceHistoryQuery"/> parameters for GET /api/v1/market/{symbol}/history.
/// </summary>
public sealed class PriceHistoryQueryValidator : AbstractValidator<PriceHistoryQuery>
{
    private static readonly HashSet<string> SupportedIntervals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "1day", "1d", "daily",
        "1min", "1m",
        "5min", "5m",
        "15min", "15m",
        "30min", "30m",
        "60min", "60m", "1h"
    };

    public PriceHistoryQueryValidator()
    {
        RuleFor(x => x.Symbol)
            .NotEmpty()
            .WithMessage("Symbol is required.")
            .MaximumLength(10)
            .WithMessage("Symbol must not exceed 10 characters.");

        RuleFor(x => x.Interval)
            .NotEmpty()
            .Must(interval => SupportedIntervals.Contains(interval.Trim()))
            .WithMessage("Interval must be one of: 1day, 1min, 5min, 15min, 30min, 60min.");

        RuleFor(x => x.Limit)
            .InclusiveBetween(1, 500)
            .WithMessage("Limit must be between 1 and 500.");

        RuleFor(x => x)
            .Must(q => !q.From.HasValue || !q.To.HasValue || q.From.Value <= q.To.Value)
            .WithMessage("The 'from' timestamp must be less than or equal to the 'to' timestamp.");
    }
}

/// <summary>
/// Validates <see cref="SymbolsFilterQuery"/> parameters for GET /api/v1/market/symbols.
/// </summary>
public sealed class SymbolsFilterQueryValidator : AbstractValidator<SymbolsFilterQuery>
{
    public SymbolsFilterQueryValidator()
    {
        RuleFor(x => x.Limit)
            .InclusiveBetween(1, 500)
            .WithMessage("Limit must be between 1 and 500.");

        RuleFor(x => x.Exchange)
            .MaximumLength(20)
            .When(x => !string.IsNullOrWhiteSpace(x.Exchange));

        RuleFor(x => x.Sector)
            .MaximumLength(80)
            .When(x => !string.IsNullOrWhiteSpace(x.Sector));
    }
}

