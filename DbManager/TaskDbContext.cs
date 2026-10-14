using Microsoft.EntityFrameworkCore;
using TaskApi.Models;
namespace TaskApi.Data;

public sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var task = builder.Entity<TaskItem>();
        task.Property(x => x.Title).IsRequired().HasMaxLength(200);
        // Npgsql maps uint + IsRowVersion to PostgreSQL's automatic xmin column.
        task.Property(x => x.RowVersion).IsRowVersion();
    }
}
