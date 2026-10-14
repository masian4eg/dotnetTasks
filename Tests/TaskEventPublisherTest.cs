using Microsoft.Extensions.Logging.Abstractions;
using TaskApi.Messaging;
using TaskApi.Models;
using Xunit;

namespace TaskApi.Tests;

public sealed class TaskEventPublisherTest
{
    [Fact]
    public async Task Failed_publication_does_not_prevent_the_next_event_from_being_sent()
    {
        var failed = Event("Failed");
        var next = Event("Next");
        var transport = new RecordingTransport { FailFor = failed.TaskId };
        var publisher = new TaskEventPublisher(transport, NullLogger<TaskEventPublisher>.Instance);
        await publisher.StartAsync(default);
        publisher.Enqueue(failed);
        publisher.Enqueue(next);
        await publisher.StopAsync(default);
        Assert.Equal(new[] { failed, next }, transport.Attempts);
        Assert.Equal(next, Assert.Single(transport.Sent));
    }

    [Fact]
    public async Task Shutdown_sends_pending_events_in_order_before_closing_transport()
    {
        var first = Event("First");
        var second = Event("Second");
        var transport = new RecordingTransport();
        var publisher = new TaskEventPublisher(transport, NullLogger<TaskEventPublisher>.Instance);
        publisher.Enqueue(first);
        publisher.Enqueue(second);
        await publisher.StartAsync(default);
        await publisher.StopAsync(default);
        Assert.Equal(new[] { first, second }, transport.Sent);
        Assert.True(transport.Closed);
        Assert.False(transport.PublishedAfterClose);
    }

    private static TaskCompletedEvent Event(string title) => new(Guid.NewGuid(), title, DateTimeOffset.UtcNow, Priority.Medium);

    private sealed class RecordingTransport : ITaskEventTransport
    {
        public Guid? FailFor { get; init; }
        public List<TaskCompletedEvent> Attempts { get; } = [];
        public List<TaskCompletedEvent> Sent { get; } = [];
        public bool Closed { get; private set; }
        public bool PublishedAfterClose { get; private set; }
        public Task PublishAsync(TaskCompletedEvent message, CancellationToken ct)
        {
            PublishedAfterClose |= Closed;
            Attempts.Add(message);
            if (message.TaskId == FailFor) throw new IOException("Simulated broker outage");
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task CloseAsync(CancellationToken ct)
        {
            Closed = true;
            return Task.CompletedTask;
        }
    }
}
