using System.Text;
using System.Text.Json;
using FinSightIQ.BuildingBlocks.Middleware;
using FinSightIQ.Contracts.Events;
using FinSightIQ.MarketDataService.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FinSightIQ.MarketDataService.Infrastructure.Messaging;

/// <summary>
/// Publishes <see cref="PriceUpdated"/> events to the RabbitMQ topic exchange 'market.price.updated'.
/// Handles broker unavailability gracefully without crashing ingestion or API requests.
/// </summary>
public sealed class RabbitMqPriceEventPublisher : IPriceEventPublisher, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConnectionFactory _connectionFactory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPriceEventPublisher> _logger;
    private readonly object _syncLock = new();

    private IConnection? _connection;
    private IModel? _channel;
    private bool _exchangeDeclared;
    private bool _disposed;

    public RabbitMqPriceEventPublisher(
        IOptions<RabbitMqOptions> options,
        IConfiguration configuration,
        ILogger<RabbitMqPriceEventPublisher> logger,
        IConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
        _connectionFactory = connectionFactory ?? CreateConnectionFactory(_options, configuration);
    }

    public Task PublishPriceUpdatedAsync(
        PriceUpdated priceUpdatedEvent,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(priceUpdatedEvent);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(priceUpdatedEvent, JsonOptions);
            string effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
                ? CorrelationId.NewId().Value
                : correlationId;

            lock (_syncLock)
            {
                IModel channel = EnsureChannelLocked();

                IBasicProperties properties = channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.ContentType = "application/json";
                properties.ContentEncoding = "utf-8";
                properties.MessageId = Guid.NewGuid().ToString("N");
                properties.CorrelationId = effectiveCorrelationId;
                properties.Timestamp = new AmqpTimestamp(priceUpdatedEvent.Timestamp.ToUnixTimeSeconds());
                properties.Type = nameof(PriceUpdated);
                properties.Headers = new Dictionary<string, object>
                {
                    [CorrelationId.HeaderName] = Encoding.UTF8.GetBytes(effectiveCorrelationId),
                    ["symbol"] = Encoding.UTF8.GetBytes(priceUpdatedEvent.Symbol)
                };

                channel.BasicPublish(
                    exchange: _options.ExchangeName,
                    routingKey: _options.RoutingKey,
                    mandatory: false,
                    basicProperties: properties,
                    body: body);
            }

            _logger.LogDebug(
                "Published PriceUpdated event for {Symbol} at {Price} to exchange {Exchange}.",
                priceUpdatedEvent.Symbol,
                priceUpdatedEvent.Price,
                _options.ExchangeName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish PriceUpdated event for {Symbol} to RabbitMQ exchange {Exchange}.",
                priceUpdatedEvent.Symbol,
                _options.ExchangeName);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseConnectionLocked();
        }
    }

    private IModel EnsureChannelLocked()
    {
        if (_connection is null || !_connection.IsOpen)
        {
            CloseConnectionLocked();
            _connection = _connectionFactory.CreateConnection();
            _exchangeDeclared = false;
        }

        if (_channel is null || !_channel.IsOpen)
        {
            _channel?.Dispose();
            _channel = _connection.CreateModel();
            _exchangeDeclared = false;
        }

        if (!_exchangeDeclared)
        {
            _channel.ExchangeDeclare(
                exchange: _options.ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null);

            _exchangeDeclared = true;
        }

        return _channel;
    }

    private void CloseConnectionLocked()
    {
        try
        {
            _channel?.Dispose();
        }
        catch
        {
            // Ignore disposal errors during reconnect.
        }
        finally
        {
            _channel = null;
        }

        try
        {
            _connection?.Dispose();
        }
        catch
        {
            // Ignore disposal errors during reconnect.
        }
        finally
        {
            _connection = null;
            _exchangeDeclared = false;
        }
    }

    private static IConnectionFactory CreateConnectionFactory(
        RabbitMqOptions options,
        IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString("RabbitMq")
            ?? configuration.GetConnectionString("RabbitMQ");

        if (!string.IsNullOrWhiteSpace(connectionString) &&
            Uri.TryCreate(connectionString, UriKind.Absolute, out Uri? amqpUri))
        {
            return new ConnectionFactory
            {
                Uri = amqpUri,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
            };
        }

        return new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            UserName = options.UserName,
            Password = options.Password,
            VirtualHost = options.VirtualHost,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };
    }
}
