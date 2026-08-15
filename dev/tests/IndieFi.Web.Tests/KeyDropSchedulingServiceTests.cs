using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropSchedulingServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropSchedulingServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ScheduleAsync_WithValidKeys_CreatesDropAndTrimmedInventoryTogether()
    {
        var (ownerId, gameId) = await CreateOwnerAndGameAsync();
        var opensAt = DateTimeOffset.UtcNow.AddHours(1);

        ScheduleKeyDropResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new KeyDropSchedulingService(serviceDb).ScheduleAsync(
                gameId,
                ownerId,
                opensAt,
                " KEY-ONE \r\n\nKEY-TWO\n   ");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var drop = await verificationDb.KeyDrops.SingleAsync(
            item => item.Id == result.KeyDropId,
            TestContext.Current.CancellationToken);
        var keys = await verificationDb.GameKeys
            .Where(key => key.DropId == drop.Id)
            .OrderBy(key => key.Id)
            .Select(key => key.KeyValue)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScheduleKeyDropOutcome.Scheduled, result.Outcome);
        Assert.Equal(2, result.KeyCount);
        Assert.Equal(ownerId, drop.OwnerId);
        Assert.Equal(gameId, drop.GameId);
        Assert.Equal(2, drop.TotalKeys);
        Assert.InRange(
            (drop.OpensAt - opensAt).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
        Assert.Equal(["KEY-ONE", "KEY-TWO"], keys);
    }

    [Fact]
    public async Task ScheduleAsync_WithNoUsableKeys_WritesNothing()
    {
        var (ownerId, gameId) = await CreateOwnerAndGameAsync();

        ScheduleKeyDropResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new KeyDropSchedulingService(serviceDb).ScheduleAsync(
                gameId,
                ownerId,
                DateTimeOffset.UtcNow.AddHours(1),
                " \r\n\n   ");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var dropCount = await verificationDb.KeyDrops.CountAsync(
            drop => drop.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ScheduleKeyDropOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [KeyDropSchedulingService.KeysField],
            result.Violations.Select(violation => violation.Field));
        Assert.Equal(0, dropCount);
    }

    [Fact]
    public async Task ScheduleAsync_WhenOpenTimeIsNotFuture_WritesNothing()
    {
        var (ownerId, gameId) = await CreateOwnerAndGameAsync();

        ScheduleKeyDropResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new KeyDropSchedulingService(serviceDb).ScheduleAsync(
                gameId,
                ownerId,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "KEY-ONE");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var dropCount = await verificationDb.KeyDrops.CountAsync(
            drop => drop.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ScheduleKeyDropOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [KeyDropSchedulingService.OpensAtField],
            result.Violations.Select(violation => violation.Field));
        Assert.Equal(0, dropCount);
    }

    [Fact]
    public async Task ScheduleAsync_WhenKeysAndOpenTimeAreBothWrong_ReportsBothAtOnce()
    {
        var (ownerId, gameId) = await CreateOwnerAndGameAsync();

        ScheduleKeyDropResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new KeyDropSchedulingService(serviceDb).ScheduleAsync(
                gameId,
                ownerId,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "   ");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var dropCount = await verificationDb.KeyDrops.CountAsync(
            drop => drop.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ScheduleKeyDropOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [KeyDropSchedulingService.KeysField, KeyDropSchedulingService.OpensAtField],
            result.Violations.Select(violation => violation.Field));
        Assert.Equal(0, dropCount);
    }

    [Fact]
    public async Task ScheduleAsync_WithDuplicateKeys_RejectsWholeBatchWithoutExposingKey()
    {
        var (ownerId, gameId) = await CreateOwnerAndGameAsync();
        const string secretKey = "DO-NOT-EXPOSE-THIS-KEY";

        ScheduleKeyDropResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new KeyDropSchedulingService(serviceDb).ScheduleAsync(
                gameId,
                ownerId,
                DateTimeOffset.UtcNow.AddHours(1),
                $"{secretKey}\n  {secretKey}  ");
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(ScheduleKeyDropOutcome.Invalid, result.Outcome);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(KeyDropSchedulingService.KeysField, violation.Field);
        Assert.Contains("2번째 줄", violation.Message);
        Assert.DoesNotContain(secretKey, violation.Message);
        Assert.False(await verificationDb.KeyDrops.AnyAsync(
            drop => drop.GameId == gameId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.GameKeys.AnyAsync(
            key => key.KeyValue == secretKey,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScheduleAsync_WhenCallerDoesNotOwnGame_WritesNothing()
    {
        var (ownerId, gameId) = await CreateOwnerAndGameAsync();
        long otherUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherUserId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        ScheduleKeyDropResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new KeyDropSchedulingService(serviceDb).ScheduleAsync(
                gameId,
                otherUserId,
                DateTimeOffset.UtcNow.AddHours(1),
                "KEY-ONE");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var dropCount = await verificationDb.KeyDrops.CountAsync(
            drop => drop.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(ownerId, otherUserId);
        Assert.Equal(ScheduleKeyDropOutcome.NotOwner, result.Outcome);
        Assert.Equal(0, dropCount);
    }

    private async Task<(long OwnerId, long GameId)> CreateOwnerAndGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        return (owner.Id, game.Id);
    }
}
