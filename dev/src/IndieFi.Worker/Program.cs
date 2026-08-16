using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
var databaseConnectionString =
    builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(databaseConnectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Database is required for the notification worker.");
}

var rabbitMq = builder.Configuration
    .GetSection(RabbitMqOptions.SectionName)
    .Get<RabbitMqOptions>() ?? new RabbitMqOptions();
if (!rabbitMq.Enabled)
{
    throw new InvalidOperationException(
        "Messaging:RabbitMq:Enabled must be true for the notification worker.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(databaseConnectionString);
    options.EnableSensitiveDataLogging(false);
});
builder.Services.AddNotificationsModule(
    builder.Configuration,
    registration => registration.AddNotificationConsumers());

await builder.Build().RunAsync();
