using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TaskApi.Data;
using TaskApi.Endpoints;
using TaskApi.Messaging;
using TaskApi.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<Priority>(allowIntegerValues: false)));
builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Tasks")));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMQ"));
builder.Services.AddSingleton<ITaskEventTransport, RabbitMqTransport>();
builder.Services.AddSingleton<TaskEventPublisher>();
builder.Services.AddSingleton<ITaskEventPublisher>(sp => sp.GetRequiredService<TaskEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskEventPublisher>());
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

var app = builder.Build();
// Инициализация схемы вместо мигарции
if (app.Configuration.GetValue("Database:Initialize", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Database.EnsureCreatedAsync();
}

app.MapTaskEndpoints();
app.Run();

public partial class Program;

