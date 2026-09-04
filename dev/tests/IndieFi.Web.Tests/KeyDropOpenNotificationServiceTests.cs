using System.Diagnostics;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropOpenNotificationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropOpenNotificationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StageDue_ClaimsOnceAndStagesStableOpenFanout()
    {
        var drop = await CreateDropAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var publisher = new TestKeyDropNotificationFanoutPublisher();
        await using var db = _fixture.NewDbContext();
        var service = new KeyDropOpenNotificationService(db, publisher);

        var first = await service.StageDueAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var replay = await service.StageDueAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, first.StagedCount);
        Assert.Equal(0, replay.StagedCount);
        var staged = Assert.Single(publisher.OpenEvents);
        Assert.Equal(drop.DropId, staged.DropId);
        Assert.Equal(drop.OpensAt, staged.OccurredAt);
        Assert.Equal(
            NotificationEventId.Create(
                NotificationKind.KeyDropOpened,
                drop.DropId,
                drop.OwnerId),
            staged.FanoutId);
        Assert.NotNull(await db.KeyDrops
            .Where(item => item.Id == drop.DropId)
            .Select(item => item.OpenNotificationStagedAt)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StageDue_FutureOrClosedDrop_IsIgnored()
    {
        var future = await CreateDropAsync(DateTimeOffset.UtcNow.AddHours(1));
        var closed = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            closeEarly: true);
        var publisher = new TestKeyDropNotificationFanoutPublisher();
        await using var db = _fixture.NewDbContext();

        var result = await new KeyDropOpenNotificationService(
            db,
            publisher).StageDueAsync(
                cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.StagedCount);
        Assert.Empty(publisher.OpenEvents);
        Assert.Null(await db.KeyDrops
            .Where(item => item.Id == future.DropId)
            .Select(item => item.OpenNotificationStagedAt)
            .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Null(await db.KeyDrops
            .Where(item => item.Id == closed.DropId)
            .Select(item => item.OpenNotificationStagedAt)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StageDue_WhenFanoutStagingFails_RollsBackClaim()
    {
        var drop = await CreateDropAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using (var db = _fixture.NewDbContext())
        {
            var service = new KeyDropOpenNotificationService(
                db,
                new TestKeyDropNotificationFanoutPublisher
                {
                    Failure = new InvalidOperationException("staging failed"),
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageDueAsync(
                cancellationToken: TestContext.Current.CancellationToken));
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Null(await verificationDb.KeyDrops
            .Where(item => item.Id == drop.DropId)
            .Select(item => item.OpenNotificationStagedAt)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StageDue_ConcurrentInstances_ClaimOneFanout()
    {
        await CreateDropAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var firstPublisher = new TestKeyDropNotificationFanoutPublisher();
        var secondPublisher = new TestKeyDropNotificationFanoutPublisher();
        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();

        var results = await Task.WhenAll(
            new KeyDropOpenNotificationService(firstDb, firstPublisher)
                .StageDueAsync(cancellationToken: TestContext.Current.CancellationToken),
            new KeyDropOpenNotificationService(secondDb, secondPublisher)
                .StageDueAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, results.Sum(item => item.StagedCount));
        Assert.Equal(1, firstPublisher.OpenEvents.Count + secondPublisher.OpenEvents.Count);
    }

    [Fact]
    public async Task Worker_StagesOpenFanoutWithoutHttpRequest()
    {
        var drop = await CreateDropAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        var publisher = new TestKeyDropNotificationFanoutPublisher();
        using var provider = BuildServiceProvider(publisher);
        var worker = new KeyDropOpenNotificationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<KeyDropOpenNotificationWorker>>(),
            TimeSpan.FromMilliseconds(50));

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10)
                && publisher.OpenEvents.Count == 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25),
                    TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(drop.DropId, Assert.Single(publisher.OpenEvents).DropId);
    }

    private ServiceProvider BuildServiceProvider(
        TestKeyDropNotificationFanoutPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton<IKeyDropNotificationFanoutPublisher>(publisher);
        services.AddScoped<KeyDropOpenNotificationService>();
        services.AddLogging();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<DropSnapshot> CreateDropAsync(
        DateTimeOffset opensAt,
        bool closeEarly = false)
    {
        await using var db = _fixture.NewDbContext();
        await db.KeyDrops
            .Where(item => item.OpenNotificationStagedAt == null
                && item.OpensAt <= DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    item => item.OpenNotificationStagedAt,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = opensAt,
            ClosedAt = closeEarly ? DateTimeOffset.UtcNow : null,
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var storedOpensAt = await db.KeyDrops
            .Where(item => item.Id == drop.Id)
            .Select(item => item.OpensAt)
            .SingleAsync(TestContext.Current.CancellationToken);
        return new DropSnapshot(drop.Id, owner.Id, storedOpensAt);
    }

    private sealed record DropSnapshot(
        long DropId,
        long OwnerId,
        DateTimeOffset OpensAt);
}
