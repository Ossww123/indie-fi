using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationEmailDeliveryAuthorityServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public NotificationEmailDeliveryAuthorityServiceTests(
        PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimNextDue_SkipsRowLockedByAnotherWorker()
    {
        await ResetNotificationsAsync();
        await ArrangeFollowedGameDeliveryAsync("locked@example.test");

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        await using var firstTransaction = await firstDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await using var secondTransaction = await secondDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);

        var first = await NewService(firstDb).ClaimNextDueAsync(
            Now,
            TestContext.Current.CancellationToken);
        var second = await NewService(secondDb).ClaimNextDueAsync(
            Now,
            TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Null(second);
        await secondTransaction.RollbackAsync(TestContext.Current.CancellationToken);
        await firstTransaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Prepare_DecryptsRecipientOnlyForCurrentRelationship()
    {
        await ResetNotificationsAsync();
        const string email = "recipient@example.test";
        await ArrangeFollowedGameDeliveryAsync(email);

        await using var db = _fixture.NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var service = NewService(db);
        var claimed = await service.ClaimNextDueAsync(
            Now,
            TestContext.Current.CancellationToken);
        var prepared = await service.PrepareAsync(
            claimed!,
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationEmailPreparationOutcome.Ready, prepared.Outcome);
        Assert.Equal(email, prepared.Request!.RecipientAddress);
        Assert.Equal(claimed!.MessageId, prepared.Request.MessageId);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Prepare_AfterFollowRemoval_ExpiresWithoutDecryptingRecipient()
    {
        await ResetNotificationsAsync();
        var arranged = await ArrangeFollowedGameDeliveryAsync("removed@example.test");
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.UserFollows
                .Where(item => item.FollowerId == arranged.UserId
                    && item.FollowedUserId == arranged.OwnerId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var service = NewService(db);
        var claimed = await service.ClaimNextDueAsync(
            Now,
            TestContext.Current.CancellationToken);
        var prepared = await service.PrepareAsync(
            claimed!,
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationEmailPreparationOutcome.Expired, prepared.Outcome);
        Assert.Null(prepared.Request);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private NotificationEmailDeliveryAuthorityService NewService(AppDbContext db)
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new NotificationEmailDeliveryOptions
            {
                PublicBaseUrl = "https://indiefi.example/",
            });
        return new NotificationEmailDeliveryAuthorityService(
            db,
            TestAccountProtection.CreateService(),
            new NotificationEmailTemplateRenderer(options));
    }

    private async Task<ArrangedDelivery> ArrangeFollowedGameDeliveryAsync(
        string email)
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"owner-{Guid.NewGuid():N}@example.test");
        var recipient = TestAccountProtection.AddUser(db, email);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        db.UserFollows.Add(new UserFollow
        {
            FollowerId = recipient.Id,
            FollowedUserId = owner.Id,
        });
        var notification = new Notification
        {
            UserId = recipient.Id,
            EventId = Guid.NewGuid(),
            Kind = NotificationKind.FollowedUserGamePublished,
            SourceType = NotificationSourceTypes.Game,
            SourceId = game.Id,
            Title = "A new game",
            Body = "A followed user published a game.",
            DestinationPath = $"/Games/Detail?id={game.Id}",
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
        return new ArrangedDelivery(owner.Id, recipient.Id);
    }

    private async Task ResetNotificationsAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        await db.Notifications.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private sealed record ArrangedDelivery(long OwnerId, long UserId);
}
