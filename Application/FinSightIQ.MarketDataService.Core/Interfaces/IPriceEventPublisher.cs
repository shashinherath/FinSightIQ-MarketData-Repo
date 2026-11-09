using FinSightIQ.Contracts.Events;

namespace FinSightIQ.MarketDataService.Core.Interfaces;

/// <summary>
/// Publishes <see cref="PriceUpdated"/> domain events to the message broker.
/// </summary>
public interface IPriceEventPublisher
{
    Task PublishPriceUpdatedAsync(
        PriceUpdated priceUpdatedEvent,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}

