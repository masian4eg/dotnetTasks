using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TaskApi.Models;
namespace TaskApi.Messaging;

public interface ITaskEventPublisher { void Enqueue(TaskCompletedEvent message); }
public interface ITaskEventTransport
{
    Task PublishAsync(TaskCompletedEvent message, CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}

public sealed class TaskEventPublisher(ITaskEventTransport transport, ILogger<TaskEventPublisher> logger) : IHostedService, ITaskEventPublisher
{
    private readonly Channel<TaskCompletedEvent> queue = Channel.CreateBounded<TaskCompletedEvent>(new BoundedChannelOptions(1000)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource abort = new();
    private Task? worker;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web){ Converters = { new JsonStringEnumConverter<Priority>() } };

    public void Enqueue(TaskCompletedEvent message)
    {
        if (!queue.Writer.TryWrite(message)) 
        {
            LogDropped(message, "Queue full or stopping");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        worker = Task.Run(ProcessAsync);
        return Task.CompletedTask;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(abort.Token))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await transport.PublishAsync(message, timeout.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "RabbitMQ publication failed; event: {Event}", JsonSerializer.Serialize(message, JsonOptions));
                }
            }
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        finally
        {
            while (queue.Reader.TryRead(out var pending)) 
            {
                LogDropped(pending, "Shutdown deadline exceeded");
            }

            // Очистка также выполняется на стороне читателя, поэтому она никогда не конфликтует с активной публикацией
            using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            try 
            { 
                await transport.CloseAsync(closeDeadline.Token).WaitAsync(closeDeadline.Token); 
            }
            catch (Exception ex) 
            { 
                logger.LogWarning(ex, "RabbitMQ shutdown could not finish within deadline."); 
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        abort.CancelAfter(TimeSpan.FromSeconds(4));
        using var registration = cancellationToken.Register(() => abort.Cancel());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        if (worker is not null)
        {
            try 
            { 
                await worker.WaitAsync(deadline.Token); 
            }
            catch (OperationCanceledException) 
            { 
                logger.LogWarning("RabbitMQ drain/cleanup deadline exceeded."); 
            }
        }
    }

    private void LogDropped(TaskCompletedEvent message, string reason) =>
        logger.LogWarning("{Reason}; event: {Event}", reason, JsonSerializer.Serialize(message, JsonOptions));
}
