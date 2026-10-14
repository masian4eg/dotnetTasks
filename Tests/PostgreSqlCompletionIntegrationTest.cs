using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskApi.Messaging;
using TaskApi.Data;
using TaskApi.Models;
using Xunit;

namespace TaskApi.Tests;

public sealed class InfrastructureFactAttribute : FactAttribute
{
    public InfrastructureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_INFRASTRUCTURE_TESTS") != "1")
        {
            Skip = "Set RUN_INFRASTRUCTURE_TESTS=1 and start docker compose up -d --wait.";
        }
    }
}

public sealed class PostgreSqlCompletionIntegrationTest
{
    [InfrastructureFact]
    public async Task Concurrent_completion_saves_one_result_and_enqueues_one_event()
    {
        await using var factory = new InfrastructureFactory();
        using var client = factory.CreateClient();
        var createdResponse = await client.PostAsJsonAsync("/tasks", new { title = "Real infrastructure " + Guid.NewGuid(), priority = "High" });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<TaskResponse>())!;
        Assert.False(created.IsCompleted);
        Assert.Null(created.CompletedAt);
        try
        {
            var results = await Task.WhenAll(
                client.PutAsync($"/tasks/{created.Id}/complete", null),
                client.PutAsync($"/tasks/{created.Id}/complete", null));
            Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);
            var completed = (await results.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<TaskResponse>())!;
            Assert.True(completed.IsCompleted);
            Assert.NotNull(completed.CompletedAt);

            var message = Assert.Single(factory.Publisher.Messages);
            Assert.Equal(new TaskCompletedEvent(created.Id, created.Title, completed.CompletedAt!.Value, Priority.High), message);
            Assert.True(factory.Publisher.SawSavedTask);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var saved = await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Tasks.FindAsync(created.Id);
                Assert.NotNull(saved);
                Assert.True(saved.IsCompleted);
                Assert.Equal(completed.CompletedAt, saved.CompletedAt);
                Assert.NotEqual(0u, saved.RowVersion);
            }
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"/tasks/{created.Id}/complete", null)).StatusCode);
            Assert.Single(factory.Publisher.Messages);
        }
        finally
        {
            var options = new DbContextOptionsBuilder<TaskDbContext>()
                .UseNpgsql(InfrastructureFactory.ConnectionString).Options;
            await using var db = new TaskDbContext(options);
            await db.Tasks.Where(t => t.Id == created.Id).ExecuteDeleteAsync();
        }
    }
}

internal sealed class InfrastructureFactory : WebApplicationFactory<Program>
{
    internal const string ConnectionString = "Host=localhost;Port=5433;Database=tasks;Username=tasks;Password=tasks";
    private readonly CompletionBarrier barrier = new();
    public RecordingPublisher Publisher { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TaskDbContext>();
            services.RemoveAll<DbContextOptions<TaskDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<TaskDbContext>>();
            services.AddDbContext<TaskDbContext>(options => options.UseNpgsql(ConnectionString).AddInterceptors(barrier));
            services.RemoveAll<ITaskEventPublisher>();
            services.AddSingleton<ITaskEventPublisher>(sp => { Publisher.Services = sp; return Publisher; });
        });
    }
}

internal sealed class CompletionBarrier : SaveChangesInterceptor
{
    private int arrivals;
    private readonly TaskCompletionSource bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<TaskItem>().Any(e => e.State == EntityState.Modified && e.Entity.IsCompleted))
        {
            if (Interlocked.Increment(ref arrivals) == 2) 
            {
                bothReady.TrySetResult();
            }

            await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        return result;
    }
}



