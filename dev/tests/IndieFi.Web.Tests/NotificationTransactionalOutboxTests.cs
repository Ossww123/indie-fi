using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationTransactionalOutboxTests
{
    private readonly PostgreSqlFixture _fixture;

    public NotificationTransactionalOutboxTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StageAndSave_PersistsDomainChangeAndIdentifierOnlyMessageTogether()
    {
        var userId = await CreateUserAsync();
        var eventId = Guid.NewGuid();
        await using var provider = BuildServices();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publisher = scope.ServiceProvider
                .GetRequiredService<INotificationEventPublisher>();

            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = userId,
                Kind = NotificationKind.ReviewAssigned,
                WebEnabled = true,
                EmailEnabled = false,
            });
            await publisher.StageAsync(
                eventId,
                userId,
                NotificationKind.ReviewAssigned,
                "review_assignment",
                sourceId: 41,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.True(await verificationDb.NotificationPreferences.AnyAsync(
            item => item.UserId == userId
                && item.Kind == NotificationKind.ReviewAssigned,
            TestContext.Current.CancellationToken));

        var stored = await verificationDb.Set<OutboxMessage>()
            .SingleAsync(
                item => item.MessageId == eventId,
                TestContext.Current.CancellationToken);
        Assert.Contains(nameof(UserNotificationRequestedV1), stored.MessageType);
        Assert.Contains("review_assignment", stored.Body);
        Assert.DoesNotContain("Title", stored.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Body", stored.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerRollback_RemovesDomainChangeAndOutboxMessageTogether()
    {
        var userId = await CreateUserAsync();
        var eventId = Guid.NewGuid();
        await using var provider = BuildServices();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var publisher = scope.ServiceProvider
                .GetRequiredService<INotificationEventPublisher>();
            await using var transaction = await db.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);

            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = userId,
                Kind = NotificationKind.ReviewExpired,
                WebEnabled = true,
                EmailEnabled = false,
            });
            await publisher.StageAsync(
                eventId,
                userId,
                NotificationKind.ReviewExpired,
                "review_assignment",
                sourceId: 42,
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.NotificationPreferences.AnyAsync(
            item => item.UserId == userId
                && item.Kind == NotificationKind.ReviewExpired,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.Set<OutboxMessage>().AnyAsync(
            item => item.MessageId == eventId,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, "review_assignment", 1)]
    [InlineData(1, "Review Assignment", 1)]
    [InlineData(1, "review_assignment", 0)]
    public async Task StageAsync_RejectsInvalidRoutingIdentifiers(
        long recipientUserId,
        string sourceType,
        long sourceId)
    {
        await using var provider = BuildServices();
        await using var scope = provider.CreateAsyncScope();
        var publisher = scope.ServiceProvider
            .GetRequiredService<INotificationEventPublisher>();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => publisher.StageAsync(
            Guid.NewGuid(),
            recipientUserId,
            NotificationKind.ReviewAssigned,
            sourceType,
            sourceId,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<long> CreateUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        return (await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0)).Id;
    }
}
