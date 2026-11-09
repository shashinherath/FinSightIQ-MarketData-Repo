namespace FinSightIQ.MarketDataService.Infrastructure.Messaging;

/// <summary>
/// Configuration options for RabbitMQ event publishing.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public string ExchangeName { get; set; } = "market.price.updated";
    public string RoutingKey { get; set; } = "market.price.updated";
}

