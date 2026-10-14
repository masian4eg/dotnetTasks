using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskApi.Data;
using TaskApi.Messaging;
using TaskApi.Models;
using Xunit;

namespace TaskApi.Tests;

public sealed class CompleteTaskIntegrationTest
{
    [Fact]
    public async Task Failed_save_returns_conflict_without_enqueuing_event()
    {
        await using var factory = new TaskApiFactory { RejectCompletion = true };
        using var client = factory.CreateClient();
        var task = await CreateTask(client);
        using var response = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Publisher.Messages);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.FindAsync(task.Id);
        Assert.False(saved!.IsCompleted);
        Assert.Null(saved.CompletedAt);
    }

    [Fact]
    public async Task Failed_transport_does_not_undo_completion()
    {
        await using var factory = new TaskApiFactory { UseFailingTransport = true };
        using var client = factory.CreateClient();
        var task = await CreateTask(client);
        using var response = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.FailingTransport.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Await the worker so the failure has been handled before checking persistence.
        await factory.Services.GetRequiredService<TaskEventPublisher>().StopAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.FindAsync(task.Id);
        Assert.True(saved!.IsCompleted);
        Assert.NotNull(saved.CompletedAt);
    }

    [Fact]
    public async Task Create_saves_an_incomplete_task_with_requested_priority()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/tasks", new { title = "Buy milk", priority = "High" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = (await response.Content.ReadFromJsonAsync<TaskResponse>())!;
        Assert.Equal($"/tasks/{task.Id}", response.Headers.Location!.OriginalString);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.FindAsync(task.Id);
        Assert.NotNull(saved);
        Assert.Equal("Buy milk", saved.Title);
        Assert.Equal(Priority.High, saved.Priority);
        Assert.False(saved.IsCompleted);
        Assert.Null(saved.CompletedAt);
    }

    [Fact]
    public async Task Complete_saves_state_before_enqueuing_matching_event()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        var task = await CreateTask(client);
        using var response = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completed = (await response.Content.ReadFromJsonAsync<TaskResponse>())!;
        Assert.True(completed.IsCompleted);
        Assert.NotNull(completed.CompletedAt);
        Assert.True(completed.CompletedAt >= task.CreatedAt);
        Assert.Equal(new TaskCompletedEvent(task.Id, task.Title, completed.CompletedAt.Value, Priority.High),
            Assert.Single(factory.Publisher.Messages));
        Assert.True(factory.Publisher.SawSavedTask);
    }

    [Fact]
    public async Task Repeated_completion_conflicts_without_changing_date_or_enqueuing_another_event()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        var task = await CreateTask(client);
        using var first = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var completed = (await first.Content.ReadFromJsonAsync<TaskResponse>())!;
        using var repeated = await client.PutAsync($"/tasks/{task.Id}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.FindAsync(task.Id);
        Assert.Equal(completed.CompletedAt, saved!.CompletedAt);
        Assert.Single(factory.Publisher.Messages);
    }

    [Fact]
    public async Task Delete_removes_the_task()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        var task = await CreateTask(client);
        using var response = await client.DeleteAsync($"/tasks/{task.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.FindAsync(task.Id));
    }

    [Fact]
    public async Task List_returns_created_tasks_in_creation_order()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        var first = await CreateTask(client);
        var second = await CreateTask(client);
        var tasks = (await client.GetFromJsonAsync<List<TaskResponse>>("/tasks"))!;
        Assert.Equal(new[] { first, second }, tasks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    public async Task Blank_title_returns_400(string? title)
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/tasks", new { title });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(200, HttpStatusCode.Created)]
    [InlineData(201, HttpStatusCode.BadRequest)]
    public async Task Title_length_limit_is_enforced(int length, HttpStatusCode expected)
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/tasks", new { title = new string('a', length) });
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Omitted_priority_defaults_to_medium()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/tasks", new { title = "Default priority" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Medium", (await response.Content.ReadFromJsonAsync<TaskResponse>())!.Priority);
    }

    [Theory]
    [InlineData("{\"title\":\"test\",\"priority\":\"Urgent\"}")]
    [InlineData("{\"title\":\"test\",\"priority\":1}")]
    public async Task Invalid_priority_returns_400(string json)
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/tasks", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_task_id_returns_400()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PutAsync("/tasks/invalid/complete", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Completing_missing_task_returns_404_without_enqueuing_event()
    {
        await using var factory = new TaskApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PutAsync($"/tasks/{Guid.NewGuid()}/complete", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Publisher.Messages);
    }

    private static async Task<TaskResponse> CreateTask(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/tasks", new { title = "Buy milk", priority = "High" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>())!;
    }
}

internal sealed record TaskResponse(Guid Id, string Title, bool IsCompleted, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string Priority);

internal sealed class TaskApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();
    public RecordingPublisher Publisher { get; } = new();
    public bool RejectCompletion { get; init; }
    public bool UseFailingTransport { get; init; }
    public FailingTransport FailingTransport { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TaskDbContext>();
            services.RemoveAll<DbContextOptions<TaskDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<TaskDbContext>>();
            services.AddDbContext<TaskDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
                if (RejectCompletion) options.AddInterceptors(new RejectCompletionInterceptor());
            });
            if (UseFailingTransport)
            {
                services.RemoveAll<ITaskEventTransport>();
                services.AddSingleton<ITaskEventTransport>(FailingTransport);
            }
            else
            {
                services.RemoveAll<ITaskEventPublisher>();
                services.AddSingleton<ITaskEventPublisher>(sp => { Publisher.Services = sp; return Publisher; });
            }
        });
    }
}

internal sealed class RejectCompletionInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<TaskItem>().Any(e => e.State == EntityState.Modified && e.Entity.IsCompleted))
            throw new DbUpdateConcurrencyException("Simulated concurrent update");
        return ValueTask.FromResult(result);
    }
}

internal sealed class FailingTransport : ITaskEventTransport
{
    public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task PublishAsync(TaskCompletedEvent message, CancellationToken ct)
    {
        Attempted.TrySetResult();
        throw new IOException("Simulated broker outage");
    }
    public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class RecordingPublisher : ITaskEventPublisher
{
    public List<TaskCompletedEvent> Messages { get; } = [];
    public IServiceProvider? Services { get; set; }
    public bool SawSavedTask { get; private set; }
    public void Enqueue(TaskCompletedEvent message)
    {
        if (Services is not null)
        {
            using var scope = Services.CreateScope();
            var task = scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.Find(message.TaskId);
            SawSavedTask = task is { IsCompleted: true } && task.CompletedAt == message.CompletedAt;
        }
        lock (Messages) Messages.Add(message);
    }
}
