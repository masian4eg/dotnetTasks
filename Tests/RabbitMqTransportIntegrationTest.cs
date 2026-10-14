using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TaskApi.Messaging;
using TaskApi.Models;
using Xunit;

namespace TaskApi.Tests;

public sealed class RabbitMqTransportIntegrationTest
{
    [InfrastructureFact]
    public async Task Publish_routes_event_with_expected_json_and_delivery_properties()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await using var connection = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync("task.events", ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        var queue = await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true, cancellationToken: ct);
        await channel.QueueBindAsync(queue.QueueName, "task.events", "task.completed", cancellationToken: ct);
        var transport = new RabbitMqTransport(Options.Create(new RabbitMqOptions()));
        var message = new TaskCompletedEvent(Guid.NewGuid(), "Transport contract", DateTimeOffset.UtcNow, Priority.High);
        try
        {
            await transport.PublishAsync(message, ct);
            BasicGetResult? received;

            while (true)
            {
                received = await channel.BasicGetAsync(queue.QueueName, autoAck: true, cancellationToken: ct);
                if (received is not null)
                {
                    using var candidate = JsonDocument.Parse(received.Body);
                    if (candidate.RootElement.GetProperty("taskId").GetGuid() == message.TaskId) 
                    {
                        break;
                    }
                }
                await Task.Delay(50, ct);
            }
            Assert.Equal("task.events", received.Exchange);
            Assert.Equal("task.completed", received.RoutingKey);
            Assert.Equal("application/json", received.BasicProperties.ContentType);
            Assert.True(received.BasicProperties.Persistent);
            using var json = JsonDocument.Parse(received.Body);
            var body = json.RootElement;
            Assert.Equal(message.TaskId, body.GetProperty("taskId").GetGuid());
            Assert.Equal(message.Title, body.GetProperty("title").GetString());
            Assert.Equal(message.CompletedAt, body.GetProperty("completedAt").GetDateTimeOffset());
            Assert.Equal("High", body.GetProperty("priority").GetString());
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await transport.CloseAsync(cleanup.Token);
        }
    }
}
