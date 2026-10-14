using System.Text.Json.Serialization;
namespace TaskApi.Models;

public enum Priority { Low, Medium, High }

public sealed class TaskItem
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    [JsonIgnore]
    public uint RowVersion { get; set; }
}

public sealed record CreateTaskRequest(string? Title, Priority Priority = Priority.Medium);
public sealed record TaskCompletedEvent(Guid TaskId, string Title, DateTimeOffset CompletedAt, Priority Priority);
