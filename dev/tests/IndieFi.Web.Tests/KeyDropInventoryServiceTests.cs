using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropInventoryServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropInventoryServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AddAsync_ToScheduledDrop_AddsEncryptedInventoryAndTotalTogether()
    {
        var data = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(1),
            "INITIAL-KEY");
        var lifecycle = new RecordingWaitingRoomLifecycle();

        AddKeyInventoryResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb, lifecycle).AddAsync(
                data.DropId,
                data.OwnerId,
                " NEW-KEY-ONE \r\n\nNEW-KEY-TWO ",
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var drop = await verificationDb.KeyDrops.SingleAsync(
            item => item.Id == data.DropId,
            TestContext.Current.CancellationToken);
        var storedKeys = await verificationDb.GameKeys
            .Where(key => key.DropId == data.DropId)
            .OrderBy(key => key.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var fingerprintCount = await verificationDb.GameKeyFingerprints.CountAsync(
            fingerprint => storedKeys
                .Select(key => key.Id)
                .Contains(fingerprint.GameKeyId),
            TestContext.Current.CancellationToken);
        var protection = TestAccountProtection.CreateGameKeyProtectionService();
        var plaintexts = storedKeys.Select(key => protection.Unprotect(
            new ProtectedGameKey(
                key.Ciphertext,
                key.Nonce,
                key.AuthenticationTag,
                key.EncryptionKeyId)).Value);

        Assert.Equal(AddKeyInventoryOutcome.Added, result.Outcome);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(3, result.TotalKeys);
        Assert.Equal(3, drop.TotalKeys);
        Assert.Equal(1, drop.StateVersion);
        Assert.Equal(3, storedKeys.Count);
        Assert.Equal(3, fingerprintCount);
        Assert.Equal(
            ["INITIAL-KEY", "NEW-KEY-ONE", "NEW-KEY-TWO"],
            plaintexts);
        Assert.Empty(lifecycle.TerminalDropIds);
        Assert.Equal([data.DropId], lifecycle.InventoryChangedDropIds);
    }

    [Fact]
    public async Task AddAsync_WhenBatchContainsRegisteredKey_RollsBackNewKeysAndTotal()
    {
        const string DuplicateSecret = "GLOBALLY-REGISTERED-KEY";
        const string NewSecret = "SHOULD-NOT-BE-ADDED";
        await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(1),
            DuplicateSecret);
        var target = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(1),
            "TARGET-INITIAL-KEY");

        AddKeyInventoryResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).AddAsync(
                target.DropId,
                target.OwnerId,
                $"{NewSecret}\n{DuplicateSecret}",
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var storedDrop = await verificationDb.KeyDrops
            .Where(drop => drop.Id == target.DropId)
            .Select(drop => new { drop.TotalKeys, drop.StateVersion })
            .SingleAsync(TestContext.Current.CancellationToken);
        var inventoryCount = await verificationDb.GameKeys.CountAsync(
            key => key.DropId == target.DropId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AddKeyInventoryOutcome.Invalid, result.Outcome);
        var violation = Assert.Single(result.Violations);
        Assert.DoesNotContain(DuplicateSecret, violation.Message);
        Assert.DoesNotContain(NewSecret, violation.Message);
        Assert.Equal(1, storedDrop.TotalKeys);
        Assert.Equal(0, storedDrop.StateVersion);
        Assert.Equal(1, inventoryCount);
    }

    [Fact]
    public async Task AddAsync_WhenDropIsSoldOutOrClosed_RejectsWithoutChangingInventory()
    {
        var soldOut = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            "SOLD-OUT-KEY");
        var closed = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            "CLOSED-KEY");

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var recipient = TestAccountProtection.AddUser(
                arrangeDb,
                $"{Guid.NewGuid():N}@example.test");
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.KeyGrants.Add(new KeyGrant
            {
                DropId = soldOut.DropId,
                UserId = recipient.Id,
                KeyId = soldOut.InitialKeyId,
            });
            var closedDrop = await arrangeDb.KeyDrops.SingleAsync(
                drop => drop.Id == closed.DropId,
                TestContext.Current.CancellationToken);
            closedDrop.ClosedAt = DateTimeOffset.UtcNow;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        AddKeyInventoryResult soldOutResult;
        AddKeyInventoryResult closedResult;
        await using (var soldOutDb = _fixture.NewDbContext())
        {
            soldOutResult = await NewService(soldOutDb).AddAsync(
                soldOut.DropId,
                soldOut.OwnerId,
                "NEW-SOLD-OUT-KEY",
                TestContext.Current.CancellationToken);
        }
        await using (var closedDb = _fixture.NewDbContext())
        {
            closedResult = await NewService(closedDb).AddAsync(
                closed.DropId,
                closed.OwnerId,
                "NEW-CLOSED-KEY",
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(AddKeyInventoryOutcome.InvalidStatus, soldOutResult.Outcome);
        Assert.Equal(AddKeyInventoryOutcome.InvalidStatus, closedResult.Outcome);
        Assert.Equal(
            2,
            await verificationDb.GameKeys.CountAsync(
                key => key.DropId == soldOut.DropId || key.DropId == closed.DropId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_WhenCallerIsNotOwner_WritesNothing()
    {
        var data = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(1),
            "OWNER-ONLY-INITIAL");
        long otherUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherUserId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        AddKeyInventoryResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).AddAsync(
                data.DropId,
                otherUserId,
                "UNAUTHORIZED-KEY",
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(AddKeyInventoryOutcome.NotOwner, result.Outcome);
        Assert.Equal(
            1,
            await verificationDb.GameKeys.CountAsync(
                key => key.DropId == data.DropId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAsync_WhenDistinctBatchesRunConcurrently_PreservesBothTotals()
    {
        var data = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            "CONCURRENT-INITIAL");

        var results = await Task.WhenAll(
            AddWithNewContextAsync(data, "CONCURRENT-ONE"),
            AddWithNewContextAsync(data, "CONCURRENT-TWO"));

        await using var verificationDb = _fixture.NewDbContext();
        var storedDrop = await verificationDb.KeyDrops
            .Where(drop => drop.Id == data.DropId)
            .Select(drop => new { drop.TotalKeys, drop.StateVersion })
            .SingleAsync(TestContext.Current.CancellationToken);
        var inventoryCount = await verificationDb.GameKeys.CountAsync(
            key => key.DropId == data.DropId,
            TestContext.Current.CancellationToken);

        Assert.All(
            results,
            result => Assert.Equal(AddKeyInventoryOutcome.Added, result.Outcome));
        Assert.Equal(3, storedDrop.TotalKeys);
        Assert.Equal(2, storedDrop.StateVersion);
        Assert.Equal(3, inventoryCount);
    }

    [Fact]
    public async Task AddAsync_WhenSameKeyIsAddedToDifferentDrops_AddsItOnlyOnceGlobally()
    {
        const string SharedKey = "CONCURRENT-GLOBAL-KEY";
        var first = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(1),
            "FIRST-DROP-INITIAL");
        var second = await CreateDropAsync(
            DateTimeOffset.UtcNow.AddHours(1),
            "SECOND-DROP-INITIAL");

        var results = await Task.WhenAll(
            AddWithNewContextAsync(first, SharedKey),
            AddWithNewContextAsync(second, SharedKey));

        var sharedValue = Assert.Single(
            GameKeyPolicy.ParseBatch(SharedKey).Keys);
        var sharedFingerprint = Assert.Single(
            TestAccountProtection
                .CreateGameKeyProtectionService()
                .CreateWritableFingerprints(sharedValue));
        await using var verificationDb = _fixture.NewDbContext();
        var totals = await verificationDb.KeyDrops
            .Where(drop => drop.Id == first.DropId || drop.Id == second.DropId)
            .Select(drop => drop.TotalKeys)
            .OrderBy(total => total)
            .ToListAsync(TestContext.Current.CancellationToken);
        var fingerprintCount = await verificationDb.GameKeyFingerprints.CountAsync(
            fingerprint =>
                fingerprint.KeyId == sharedFingerprint.KeyId
                && fingerprint.Value == sharedFingerprint.Value,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            results.Count(result => result.Outcome == AddKeyInventoryOutcome.Added));
        Assert.Equal(
            1,
            results.Count(result => result.Outcome == AddKeyInventoryOutcome.Invalid));
        Assert.Equal([1, 2], totals);
        Assert.Equal(1, fingerprintCount);
    }

    private async Task<AddKeyInventoryResult> AddWithNewContextAsync(
        DropData data,
        string key)
    {
        await using var db = _fixture.NewDbContext();
        return await NewService(db).AddAsync(
            data.DropId,
            data.OwnerId,
            key,
            TestContext.Current.CancellationToken);
    }

    private async Task<DropData> CreateDropAsync(
        DateTimeOffset opensAt,
        string initialKey)
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Inventory game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = opensAt,
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var gameKey = TestAccountProtection.AddGameKey(db, drop, initialKey);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new DropData(owner.Id, drop.Id, gameKey.Id);
    }

    private static KeyDropInventoryService NewService(
        AppDbContext db,
        IWaitingRoomLifecycle? waitingRoomLifecycle = null) => new(
        db,
        new GameKeyRegistrationWriter(
            db,
            TestAccountProtection.CreateGameKeyProtectionService()),
        new KeyDropMutationLock(db),
        waitingRoomLifecycle ?? NullWaitingRoomLifecycle.Instance);

    private sealed record DropData(long OwnerId, long DropId, long InitialKeyId);
}
