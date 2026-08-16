using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationEmailMailpitIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;
    private readonly IContainer _mailpit =
        new ContainerBuilder("axllent/mailpit:v1.30.7")
            .WithPortBinding(1025, assignRandomHostPort: true)
            .WithPortBinding(8025, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(8025).ForPath("/api/v1/info")))
            .Build();

    public NotificationEmailMailpitIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _mailpit.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SmtpDelivery_ReachesMailpitWithStableHeaderAndPlainTextBody()
    {
        await ResetNotificationsAsync();
        const string recipientAddress = "mailpit-recipient@example.test";
        var arranged = await ArrangeDeliveryAsync(recipientAddress);
        var options = Microsoft.Extensions.Options.Options.Create(
            new NotificationEmailDeliveryOptions
            {
                Enabled = true,
                PublicBaseUrl = "https://indiefi.example/",
                Smtp = new NotificationSmtpOptions
                {
                    Host = _mailpit.Hostname,
                    Port = _mailpit.GetMappedPublicPort(1025),
                    EnableSsl = false,
                    FromAddress = "notifications@indiefi.local",
                    FromName = "IndieFi",
                    Timeout = TimeSpan.FromSeconds(10),
                },
            });
        var renderer = new NotificationEmailTemplateRenderer(options);
        var sender = new SmtpNotificationEmailSender(
            options,
            new NotificationEmailMessageFactory(options));

        await using (var db = _fixture.NewDbContext())
        {
            var authority = new NotificationEmailDeliveryAuthorityService(
                db,
                TestAccountProtection.CreateService(),
                renderer);
            var processor = new NotificationEmailDeliveryProcessor(
                db,
                authority,
                sender,
                new FixedTimeProvider(Now));
            var result = await processor.ProcessBatchAsync(
                10,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, result.SentCount);
        }

        using var http = new HttpClient
        {
            BaseAddress = new Uri(
                $"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}"),
        };
        var messageId = await WaitForMessageIdAsync(http);
        using var detail = JsonDocument.Parse(await http.GetStringAsync(
            $"/api/v1/message/{Uri.EscapeDataString(messageId)}",
            TestContext.Current.CancellationToken));
        var root = detail.RootElement;
        Assert.Equal("[IndieFi] Actual SMTP notification", GetString(root, "Subject"));
        Assert.Equal(
            recipientAddress,
            GetProperty(root, "To")[0].GetProperty("Address").GetString());
        Assert.Contains(
            arranged.MessageId.ToString("N"),
            GetString(root, "MessageID"),
            StringComparison.OrdinalIgnoreCase);
        var textBody = GetString(root, "Text");
        Assert.Contains("Actual SMTP body", textBody);
        Assert.Contains("https://indiefi.example/Account/Profile", textBody);
        Assert.DoesNotContain("<html", textBody, StringComparison.OrdinalIgnoreCase);

        await using var verificationDb = _fixture.NewDbContext();
        var delivery = await verificationDb.NotificationDeliveries.SingleAsync(
            item => item.Id == arranged.DeliveryId,
            TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
    }

    public async ValueTask DisposeAsync()
    {
        await _mailpit.DisposeAsync();
    }

    private async Task<string> WaitForMessageIdAsync(HttpClient http)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            using var response = await http.GetAsync(
                "/api/v1/messages",
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            using var messages = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken));
            var items = GetProperty(messages.RootElement, "messages");
            if (items.GetArrayLength() > 0)
            {
                return GetString(items[0], "ID");
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken);
        }

        Assert.Fail("Mailpit did not receive the notification email.");
        return string.Empty;
    }

    private async Task<ArrangedDelivery> ArrangeDeliveryAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var notification = new Notification
        {
            UserId = user.Id,
            EventId = Guid.NewGuid(),
            Kind = NotificationKind.AccountStatusChanged,
            SourceType = NotificationSourceTypes.User,
            SourceId = user.Id,
            Title = "Actual SMTP notification",
            Body = "Actual SMTP body",
            DestinationPath = "/Account/Profile",
            IsWebVisible = true,
            OccurredAt = Now.AddMinutes(-1),
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(90),
        };
        var delivery = new NotificationDelivery
        {
            MessageId = Guid.NewGuid(),
            Channel = NotificationChannel.Email,
            Status = NotificationDeliveryStatus.Pending,
            CreatedAt = Now,
            NextAttemptAt = Now,
        };
        notification.Deliveries.Add(delivery);
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(delivery.Id, delivery.MessageId);
    }

    private async Task ResetNotificationsAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        await db.Notifications.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value)
            || element.TryGetProperty(name.ToLowerInvariant(), out value))
        {
            return value;
        }

        Assert.Fail($"Mailpit response is missing '{name}'.");
        return default;
    }

    private static string GetString(JsonElement element, string name)
    {
        var value = GetProperty(element, name).GetString();
        Assert.NotNull(value);
        return value;
    }

    private sealed record ArrangedDelivery(long DeliveryId, Guid MessageId);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
