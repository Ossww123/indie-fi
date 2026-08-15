using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameKeyMigrationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameKeyMigrationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MigrateBatchAsync_BackfillsPlaintextAndIsIdempotentAfterRestart()
    {
        long firstGameKeyId;
        long secondGameKeyId;
        var firstValue = $"BACKFILL-FIRST-{Guid.NewGuid():N}";
        var secondValue = $"BACKFILL-SECOND-{Guid.NewGuid():N}";

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var drop = await CreateDropAsync(setupDb);
            var first = new GameKey
            {
                DropId = drop.Id,
                KeyValue = firstValue,
            };
            var second = new GameKey
            {
                DropId = drop.Id,
                KeyValue = secondValue,
            };
            setupDb.GameKeys.AddRange(first, second);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstGameKeyId = first.Id;
            secondGameKeyId = second.Id;
        }

        var provider = CreateRotationProvider();
        await using (var interruptedDb = _fixture.NewOwnerDbContext())
        {
            var interruptedService = new GameKeyMigrationService(
                interruptedDb,
                provider);
            var firstBatch = await interruptedService.MigrateBatchAsync(
                firstGameKeyId - 1,
                batchSize: 1,
                TestContext.Current.CancellationToken);

            Assert.Equal(firstGameKeyId, firstBatch.NextGameKeyId);
            Assert.Equal(1, firstBatch.EncryptedCount);
            Assert.Equal(0, firstBatch.ReencryptedCount);
            Assert.Equal(2, firstBatch.AddedFingerprintCount);
        }

        await using var resumedDb = _fixture.NewOwnerDbContext();
        var resumedService = new GameKeyMigrationService(resumedDb, provider);
        var resumed = await resumedService.MigrateBatchAsync(
            firstGameKeyId - 1,
            batchSize: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(secondGameKeyId, resumed.NextGameKeyId);
        Assert.Equal(2, resumed.ScannedCount);
        Assert.Equal(1, resumed.EncryptedCount);
        Assert.Equal(0, resumed.ReencryptedCount);
        Assert.Equal(2, resumed.AddedFingerprintCount);

        resumedDb.ChangeTracker.Clear();
        var gameKeys = await resumedDb.GameKeys
            .Where(gameKey =>
                gameKey.Id == firstGameKeyId
                || gameKey.Id == secondGameKeyId)
            .OrderBy(gameKey => gameKey.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var fingerprints = await resumedDb.GameKeyFingerprints
            .Where(fingerprint =>
                fingerprint.GameKeyId == firstGameKeyId
                || fingerprint.GameKeyId == secondGameKeyId)
            .OrderBy(fingerprint => fingerprint.GameKeyId)
            .ThenBy(fingerprint => fingerprint.KeyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(
            gameKeys,
            gameKey => Assert.Equal(
                "game-encryption-test-v2",
                gameKey.EncryptionKeyId));
        Assert.Equal(firstValue, gameKeys[0].KeyValue);
        Assert.Equal(secondValue, gameKeys[1].KeyValue);
        Assert.All(
            gameKeys,
            gameKey => Assert.Equal(
                ["game-fingerprint-test-v1", "game-fingerprint-test-v2"],
                fingerprints
                    .Where(item => item.GameKeyId == gameKey.Id)
                    .Select(item => item.KeyId)));

        var protection = new GameKeyProtectionService(provider);
        Assert.Equal(firstValue, Unprotect(protection, gameKeys[0]).Value);
        Assert.Equal(secondValue, Unprotect(protection, gameKeys[1]).Value);

        var verification = await resumedService.VerifyBatchAsync(
            firstGameKeyId - 1,
            batchSize: 10,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, verification.ScannedCount);

        resumedDb.ChangeTracker.Clear();
        var repeated = await resumedService.MigrateBatchAsync(
            firstGameKeyId - 1,
            batchSize: 10,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, repeated.ScannedCount);
        Assert.Equal(0, repeated.EncryptedCount);
        Assert.Equal(0, repeated.ReencryptedCount);
        Assert.Equal(0, repeated.AddedFingerprintCount);
    }

    [Fact]
    public async Task MigrateBatchAsync_ReencryptsOldCiphertextAndAddsNewFingerprint()
    {
        var value = ParseKey($"ROTATE-{Guid.NewGuid():N}");
        var oldProtection = TestAccountProtection.CreateGameKeyProtectionService();
        var protectedKey = oldProtection.Protect(value);
        var oldFingerprint = oldProtection
            .CreateWritableFingerprints(value)
            .Single();
        long gameKeyId;
        byte[] oldCiphertext;

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var drop = await CreateDropAsync(setupDb);
            var gameKey = new GameKey
            {
                DropId = drop.Id,
                Ciphertext = protectedKey.Ciphertext,
                Nonce = protectedKey.Nonce,
                AuthenticationTag = protectedKey.AuthenticationTag,
                EncryptionKeyId = protectedKey.EncryptionKeyId,
            };
            setupDb.GameKeys.Add(gameKey);
            setupDb.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
            {
                GameKey = gameKey,
                KeyId = oldFingerprint.KeyId,
                Value = oldFingerprint.Value,
            });
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameKeyId = gameKey.Id;
            oldCiphertext = gameKey.Ciphertext.ToArray();
        }

        var provider = CreateRotationProvider();
        await using var migrationDb = _fixture.NewOwnerDbContext();
        var service = new GameKeyMigrationService(migrationDb, provider);
        var result = await service.MigrateBatchAsync(
            gameKeyId - 1,
            batchSize: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.EncryptedCount);
        Assert.Equal(1, result.ReencryptedCount);
        Assert.Equal(1, result.AddedFingerprintCount);

        migrationDb.ChangeTracker.Clear();
        var stored = await migrationDb.GameKeys.SingleAsync(
            gameKey => gameKey.Id == gameKeyId,
            TestContext.Current.CancellationToken);
        var fingerprintKeyIds = await migrationDb.GameKeyFingerprints
            .Where(fingerprint => fingerprint.GameKeyId == gameKeyId)
            .OrderBy(fingerprint => fingerprint.KeyId)
            .Select(fingerprint => fingerprint.KeyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("game-encryption-test-v2", stored.EncryptionKeyId);
        Assert.False(oldCiphertext.SequenceEqual(stored.Ciphertext!));
        Assert.Equal(value.Value, Unprotect(
            new GameKeyProtectionService(provider),
            stored).Value);
        Assert.Equal(
            ["game-fingerprint-test-v1", "game-fingerprint-test-v2"],
            fingerprintKeyIds);
    }

    [Fact]
    public async Task MigrateBatchAsync_DuplicateAcrossBatchesStopsWithoutExposingValue()
    {
        var duplicateValue = $"DUPLICATE-SECRET-{Guid.NewGuid():N}";
        long firstGameKeyId;
        long secondGameKeyId;

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var drop = await CreateDropAsync(setupDb);
            var first = new GameKey
            {
                DropId = drop.Id,
                KeyValue = duplicateValue,
            };
            var second = new GameKey
            {
                DropId = drop.Id,
                KeyValue = duplicateValue,
            };
            setupDb.GameKeys.AddRange(first, second);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstGameKeyId = first.Id;
            secondGameKeyId = second.Id;
        }

        var provider = CreateRotationProvider();
        await using (var migrationDb = _fixture.NewOwnerDbContext())
        {
            var service = new GameKeyMigrationService(migrationDb, provider);
            var completed = await service.MigrateBatchAsync(
                firstGameKeyId - 1,
                batchSize: 1,
                TestContext.Current.CancellationToken);
            Assert.Equal(firstGameKeyId, completed.NextGameKeyId);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.MigrateBatchAsync(
                    firstGameKeyId,
                    batchSize: 1,
                    TestContext.Current.CancellationToken));

            Assert.Contains(
                secondGameKeyId.ToString(),
                exception.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                duplicateValue,
                exception.ToString(),
                StringComparison.Ordinal);
        }

        await using var verificationDb = _fixture.NewOwnerDbContext();
        var storedKeys = await verificationDb.GameKeys
            .Where(gameKey =>
                gameKey.Id == firstGameKeyId
                || gameKey.Id == secondGameKeyId)
            .OrderBy(gameKey => gameKey.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var secondFingerprintCount = await verificationDb.GameKeyFingerprints
            .CountAsync(
                fingerprint => fingerprint.GameKeyId == secondGameKeyId,
                TestContext.Current.CancellationToken);

        Assert.NotNull(storedKeys[0].Ciphertext);
        Assert.Null(storedKeys[1].Ciphertext);
        Assert.Equal(0, secondFingerprintCount);
    }

    [Fact]
    public async Task VerifyBatchAsync_MissingFingerprintFailsWithoutExposingValue()
    {
        var value = $"VERIFY-SECRET-{Guid.NewGuid():N}";
        var protection = new GameKeyProtectionService(CreateRotationProvider());
        var protectedKey = protection.Protect(ParseKey(value));
        long gameKeyId;

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var drop = await CreateDropAsync(setupDb);
            var gameKey = new GameKey
            {
                DropId = drop.Id,
                Ciphertext = protectedKey.Ciphertext,
                Nonce = protectedKey.Nonce,
                AuthenticationTag = protectedKey.AuthenticationTag,
                EncryptionKeyId = protectedKey.EncryptionKeyId,
            };
            setupDb.GameKeys.Add(gameKey);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameKeyId = gameKey.Id;
        }

        await using var verificationDb = _fixture.NewOwnerDbContext();
        var service = new GameKeyMigrationService(
            verificationDb,
            CreateRotationProvider());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VerifyBatchAsync(
                gameKeyId - 1,
                batchSize: 1,
                TestContext.Current.CancellationToken));

        Assert.Contains(
            gameKeyId.ToString(),
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            value,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static IKeyProvider CreateRotationProvider()
    {
        var values = TestAccountProtection.ConfigurationValues();
        values["AccountProtection:Purposes:game-key-encryption:CurrentKeyId"] =
            "game-encryption-test-v2";
        values["AccountProtection:Purposes:game-key-encryption:ReadKeyIds:0"] =
            "game-encryption-test-v1";
        values["AccountProtection:Purposes:game-key-encryption:ReadKeyIds:1"] =
            "game-encryption-test-v2";
        values["AccountProtection:Purposes:game-key-encryption:WriteKeyIds:0"] =
            "game-encryption-test-v2";
        values[
            "AccountProtection:Purposes:game-key-encryption:Keys:game-encryption-test-v2"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)51, 32).ToArray());

        values["AccountProtection:Purposes:game-key-fingerprint:CurrentKeyId"] =
            "game-fingerprint-test-v2";
        values["AccountProtection:Purposes:game-key-fingerprint:ReadKeyIds:0"] =
            "game-fingerprint-test-v1";
        values["AccountProtection:Purposes:game-key-fingerprint:ReadKeyIds:1"] =
            "game-fingerprint-test-v2";
        values["AccountProtection:Purposes:game-key-fingerprint:WriteKeyIds:0"] =
            "game-fingerprint-test-v1";
        values["AccountProtection:Purposes:game-key-fingerprint:WriteKeyIds:1"] =
            "game-fingerprint-test-v2";
        values[
            "AccountProtection:Purposes:game-key-fingerprint:Keys:game-fingerprint-test-v2"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)52, 32).ToArray());

        return new ConfigurationKeyProvider(
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build());
    }

    private static GameKeyValue Unprotect(
        GameKeyProtectionService protection,
        GameKey gameKey)
    {
        return protection.Unprotect(new ProtectedGameKey(
            gameKey.Ciphertext!,
            gameKey.Nonce!,
            gameKey.AuthenticationTag!,
            gameKey.EncryptionKeyId!));
    }

    private async Task<KeyDrop> CreateDropAsync(AppDbContext db)
    {
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = 2,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return drop;
    }

    private static GameKeyValue ParseKey(string value)
    {
        var result = GameKeyPolicy.ParseBatch(value);
        Assert.True(result.IsValid);
        return Assert.Single(result.Keys);
    }
}
