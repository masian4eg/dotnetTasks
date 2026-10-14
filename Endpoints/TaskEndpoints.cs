using Microsoft.EntityFrameworkCore;
using TaskApi.Data;
using TaskApi.Messaging;
using TaskApi.Models;

namespace TaskApi.Endpoints;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/tasks");
        tasks.MapPost("", async (CreateTaskRequest request, TaskDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["Title must contain text and be at most 200 characters."] });
            }

            if (!Enum.IsDefined(request.Priority))
            {
                return Results.BadRequest(new { error = "Invalid priority." });
            }

            var task = new TaskItem { Id = Guid.NewGuid(), Title = request.Title, Priority = request.Priority, CreatedAt = UtcNowForDatabase() };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/tasks/{task.Id}", task);
        });

        tasks.MapGet("", async (TaskDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Tasks.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct)));

        tasks.MapPut("/{id}/complete", async (Guid id, TaskDbContext db, ITaskEventPublisher publisher, CancellationToken ct) =>
        {
            var task = await db.Tasks.FindAsync([id], ct);
            if (task is null) 
            {
                return Results.NotFound();
            }

            if (task.IsCompleted) 
            {
                return Results.Conflict(new { error = "Task is already completed." });
            }

            task.IsCompleted = true;
            task.CompletedAt = UtcNowForDatabase();
            try 
            { 
                await db.SaveChangesAsync(ct); 
            }
            catch (DbUpdateConcurrencyException) 
            { 
                return Results.Conflict(new { error = "Task was changed by another request." }); 
            }

            publisher.Enqueue(new(task.Id, task.Title, task.CompletedAt.Value, task.Priority));

            return Results.Ok(task);
        });

        tasks.MapDelete("/{id}", async (Guid id, TaskDbContext db, CancellationToken ct) =>
        {
            var task = await db.Tasks.FindAsync([id], ct);
            if (task is null) 
            {
                return Results.NotFound();
            }

            db.Tasks.Remove(task);

            try 
            { 
                await db.SaveChangesAsync(ct); 
            }
            catch (DbUpdateConcurrencyException) 
            { 
                return Results.Conflict(); 
            }

            return Results.NoContent();
        });
    }

    private static DateTimeOffset UtcNowForDatabase()
    {
        // Метки времени PostgreSQL имеют точность до микросекунды (10 тактов .NET)
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}


