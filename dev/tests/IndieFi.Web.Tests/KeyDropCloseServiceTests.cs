using System.Data.Common;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropCloseServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropCloseServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseAsync_WhenDropCanClose_ClosesAndAdvancesVersion(
        bool active)
    {
        var data = await CreateDropAsync(active, keyCount: 2);

        CloseKeyDropResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewCloseService(serviceDb).CloseAsync(
                data.DropId,
                data.OwnerId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.KeyDrops
            .Where(drop => drop.Id == data.DropId)
            .Select(drop => new { drop.ClosedAt, drop.StateVersion })
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloseKeyDropOutcome.Closed, result.Outcome);
        Assert.NotNull(stored.ClosedAt);
        Assert.Equal(1, stored.StateVersion);
    }

    [Fact]
    public async Task CloseAsync_WhenRepeated_ReturnsAlreadyClosedWithoutChangingVersionAgain()
    {
        var data = await CreateDropAsync(active: true, keyCount: 1);

        CloseKeyDropResult first;
        CloseKeyDropResult second;
        await using (var firstDb = _fixture.NewDbContext())
        {
            first = await NewCloseService(firstDb).CloseAsync(
                data.DropId,
                data.OwnerId,
                TestContext.Current.CancellationToken);
        }
        await using (var secondDb = _fixture.NewDbContext())
        {
            second = await NewCloseService(secondDb).CloseAsync(
                data.DropId,
                data.OwnerId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var version = await verificationDb.KeyDrops
            .Where(drop => drop.Id == data.DropId)
            .Select(drop => drop.StateVersion)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloseKeyDropOutcome.Closed, first.Outcome);
        Assert.Equal(CloseKeyDropOutcome.AlreadyClosed, second.Outcome);
        Assert.Equal(1, version);
    }

    [Fact]
    public async Task CloseAsync_WhenExistingGrantExists_PreservesItAndBlocksNewClaims()
    {
        var data = await CreateDropAsync(active: true, keyCount: 2);
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.KeyGrants.Add(new KeyGrant
            {
                DropId = data.DropId,
                UserId = data.FirstRecipientId,
                KeyId = data.KeyIds[0],
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        CloseKeyDropResult close;
        await using (var closeDb = _fixture.NewDbContext())
        {
            close = await NewCloseService(closeDb).CloseAsync(
                data.DropId,
                data.OwnerId,
                TestContext.Current.CancellationToken);
        }

        ClaimResult claim;
        await using (var claimDb = _fixture.NewDbContext())
        {
            claim = await NewClaimService(claimDb).ClaimAsync(
                data.DropId,
                data.SecondRecipientId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var grants = await verificationDb.KeyGrants
            .Where(grant => grant.DropId == data.DropId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloseKeyDropOutcome.Closed, close.Outcome);
        Assert.Equal(ClaimOutcome.ClosedEarly, claim.Outcome);
        var existingGrant = Assert.Single(grants);
        Assert.Equal(data.FirstRecipientId, existingGrant.UserId);
        Assert.Equal(data.KeyIds[0], existingGrant.KeyId);
    }

    [Fact]
    public async Task CloseAsync_WhenSoldOutOrRequestedByNonOwner_DoesNotClose()
    {
        var soldOut = await CreateDropAsync(active: true, keyCount: 1);
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.KeyGrants.Add(new KeyGrant
            {
                DropId = soldOut.DropId,
                UserId = soldOut.FirstRecipientId,
                KeyId = soldOut.KeyIds[0],
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        CloseKeyDropResult soldOutResult;
        await using (var soldOutDb = _fixture.NewDbContext())
        {
            soldOutResult = await NewCloseService(soldOutDb).CloseAsync(
                soldOut.DropId,
                soldOut.OwnerId,
                TestContext.Current.CancellationToken);
        }

        var ownedDrop = await CreateDropAsync(active: false, keyCount: 1);
        CloseKeyDropResult nonOwnerResult;
        await using (var nonOwnerDb = _fixture.NewDbContext())
        {
            nonOwnerResult = await NewCloseService(nonOwnerDb).CloseAsync(
                ownedDrop.DropId,
                ownedDrop.FirstRecipientId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(CloseKeyDropOutcome.InvalidStatus, soldOutResult.Outcome);
        Assert.Equal(CloseKeyDropOutcome.NotOwner, nonOwnerResult.Outcome);
    }

    [Fact]
    public async Task CloseAsync_WhenCloseCommitsFirst_LaterClaimSeesClosedDrop()
    {
        var data = await CreateDropAsync(active: true, keyCount: 1);

        CloseKeyDropResult close;
        await using (var closeDb = _fixture.NewDbContext())
        {
            close = await NewCloseService(closeDb).CloseAsync(
                data.DropId,
                data.OwnerId,
                TestContext.Current.CancellationToken);
        }

        ClaimResult claim;
        await using (var claimDb = _fixture.NewDbContext())
        {
            claim = await NewClaimService(claimDb).ClaimAsync(
                data.DropId,
                data.FirstRecipientId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(CloseKeyDropOutcome.Closed, close.Outcome);
        Assert.Equal(ClaimOutcome.ClosedEarly, claim.Outcome);
    }

    [Fact]
    public async Task CloseAsync_WhenCloseCommitsFirst_LaterInventoryAddSeesClosedDrop()
    {
        var data = await CreateDropAsync(active: false, keyCount: 1);

        CloseKeyDropResult close;
        await using (var closeDb = _fixture.NewDbContext())
        {
            close = await NewCloseService(closeDb).CloseAsync(
                data.DropId,
                data.OwnerId,
                TestContext.Current.CancellationToken);
        }

        AddKeyInventoryResult inventory;
        await using (var inventoryDb = _fixture.NewDbContext())
        {
            inventory = await NewInventoryService(inventoryDb).AddAsync(
                data.DropId,
                data.OwnerId,
                $"AFTER-CLOSE-{Guid.NewGuid():N}",
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var drop = await verificationDb.KeyDrops
            .Where(item => item.Id == data.DropId)
            .Select(item => new { item.TotalKeys, item.StateVersion })
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CloseKeyDropOutcome.Closed, close.Outcome);
        Assert.Equal(AddKeyInventoryOutcome.InvalidStatus, inventory.Outcome);
        Assert.Equal(1, drop.TotalKeys);
        Assert.Equal(1, drop.StateVersion);
    }

    [Fact]
    public async Task CloseAsync_WhenClaimCommitsAfterCloseSnapshot_ReturnsConflict()
    {
        var data = await CreateDropAsync(active: true, keyCount: 2);
        var gate = new CloseSnapshotGate();

        await using var closeDb = _fixture.NewDbContext(gate);
        var closeTask = NewCloseService(closeDb).CloseAsync(
            data.DropId,
            data.OwnerId,
            TestContext.Current.CancellationToken);

        await gate.SnapshotRead.WaitAsync(TestContext.Current.CancellationToken);
        ClaimResult claim;
        try
        {
            await using var claimDb = _fixture.NewDbContext();
            claim = await NewClaimService(claimDb).ClaimAsync(
                data.DropId,
                data.FirstRecipientId,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Continue();
        }
        var close = await closeTask;

        await using var verificationDb = _fixture.NewDbContext();
        var drop = await verificationDb.KeyDrops
            .Where(item => item.Id == data.DropId)
            .Select(item => new { item.ClosedAt, item.StateVersion })
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClaimOutcome.Granted, claim.Outcome);
        Assert.Equal(CloseKeyDropOutcome.Conflict, close.Outcome);
        Assert.Null(drop.ClosedAt);
        Assert.Equal(1, drop.StateVersion);
    }

    [Fact]
    public async Task CloseAsync_WhenInventoryCommitsAfterCloseSnapshot_ReturnsConflict()
    {
        var data = await CreateDropAsync(active: false, keyCount: 1);
        var gate = new CloseSnapshotGate();

        await using var closeDb = _fixture.NewDbContext(gate);
        var closeTask = NewCloseService(closeDb).CloseAsync(
            data.DropId,
            data.OwnerId,
            TestContext.Current.CancellationToken);

        await gate.SnapshotRead.WaitAsync(TestContext.Current.CancellationToken);
        AddKeyInventoryResult inventory;
        try
        {
            await using var inventoryDb = _fixture.NewDbContext();
            inventory = await NewInventoryService(inventoryDb).AddAsync(
                data.DropId,
                data.OwnerId,
                $"CLOSE-RACE-{Guid.NewGuid():N}",
                TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Continue();
        }
        var close = await closeTask;

        await using var verificationDb = _fixture.NewDbContext();
        var drop = await verificationDb.KeyDrops
            .Where(item => item.Id == data.DropId)
            .Select(item => new
            {
                item.ClosedAt,
                item.StateVersion,
                item.TotalKeys,
            })
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AddKeyInventoryOutcome.Added, inventory.Outcome);
        Assert.Equal(CloseKeyDropOutcome.Conflict, close.Outcome);
        Assert.Null(drop.ClosedAt);
        Assert.Equal(1, drop.StateVersion);
        Assert.Equal(2, drop.TotalKeys);
    }

    private async Task<DropData> CreateDropAsync(bool active, int keyCount)
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        var firstRecipient = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        var secondRecipient = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Close drop game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = active
                ? DateTimeOffset.UtcNow.AddHours(-1)
                : DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = keyCount,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var keys = new List<GameKey>();
        for (var index = 0; index < keyCount; index++)
        {
            var key = TestAccountProtection.AddGameKey(
                db,
                drop,
                $"CLOSE-{Guid.NewGuid():N}");
            keys.Add(key);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new DropData(
            owner.Id,
            firstRecipient.Id,
            secondRecipient.Id,
            drop.Id,
            keys.Select(key => key.Id).ToArray());
    }

    private static KeyDropCloseService NewCloseService(AppDbContext db) =>
        new(db, new KeyDropMutationLock(db));

    private static KeyClaimService NewClaimService(AppDbContext db) =>
        new(
            db,
            new KeyDropMutationLock(db),
            NullLogger<KeyClaimService>.Instance);

    private static KeyDropInventoryService NewInventoryService(AppDbContext db) =>
        new(
            db,
            new GameKeyRegistrationWriter(
                db,
                TestAccountProtection.CreateGameKeyProtectionService()),
            new KeyDropMutationLock(db));

    private sealed record DropData(
        long OwnerId,
        long FirstRecipientId,
        long SecondRecipientId,
        long DropId,
        IReadOnlyList<long> KeyIds);

    private sealed class CloseSnapshotGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _snapshotRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed = 1;

        public Task SnapshotRead => _snapshotRead.Task;

        public void Continue() => _continue.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "state_version",
                    StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _snapshotRead.TrySetResult();
                await _continue.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
