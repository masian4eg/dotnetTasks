using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TaskApi.Models;
namespace TaskApi.Messaging;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
}

// Used exclusively by the single queue reader; channel publishes never overlap.
public sealed class RabbitMqTransport(IOptions<RabbitMqOptions> options) : ITaskEventTransport
{
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(TaskCompletedEvent message, CancellationToken ct)
    {
        if (_channel is not { IsOpen: true })
        {
            await CloseAsync(ct);
            var settings = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.HostName, Port = settings.Port, UserName = settings.UserName,
                Password = settings.Password, VirtualHost = settings.VirtualHost,
                AutomaticRecoveryEnabled = false, RequestedConnectionTimeout = TimeSpan.FromSeconds(2)
            };
            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(false, false), ct);
            await _channel.ExchangeDeclareAsync("task.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(message, TaskEventPublisher.JsonOptions);
        await _channel.BasicPublishAsync("task.events", "task.completed", mandatory: false,
            new BasicProperties { ContentType = "application/json", Persistent = true }, body, ct);
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;
        try
        {
            if (channel is not null)
            {
                try { if (channel.IsOpen) await channel.CloseAsync(ct); }
                finally { await channel.DisposeAsync(); }
            }
        }
        finally
        {
            if (connection is not null)
            {
                try { if (connection.IsOpen) await connection.CloseAsync(ct); }
                finally { await connection.DisposeAsync(); }
            }
        }
    }
}
