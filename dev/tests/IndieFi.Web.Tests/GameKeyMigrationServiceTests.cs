using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
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
    public async Task MigrateBatchAsync_BackfillsFingerprintsAndIsIdempotentAfterRestart()
    {
        long firstGameKeyId;
        long secondGameKeyId;
        var firstValue = $"BACKFILL-FIRST-{Guid.NewGuid():N}";
        var secondValue = $"BACKFILL-SECOND-{Guid.NewGuid():N}";
        var provider = CreateRotationProvider();
        var protection = new GameKeyProtectionService(provider);

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var drop = await CreateDropAsync(setupDb);
            var first = NewEncryptedKey(drop, protection, firstValue);
            var second = NewEncryptedKey(drop, protection, secondValue);
            setupDb.GameKeys.AddRange(first, second);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstGameKeyId = first.Id;
            secondGameKeyId = second.Id;
        }

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
        Assert.All(
            gameKeys,
            gameKey => Assert.Equal(
                ["game-fingerprint-test-v1", "game-fingerprint-test-v2"],
                fingerprints
                    .Where(item => item.GameKeyId == gameKey.Id)
                    .Select(item => item.KeyId)));

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
    public async Task RetireFingerprintKeyAsync_AfterVerificationKeepsDuplicateProtection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = $"game_key_retirement_{Guid.NewGuid():N}";
        var value = ParseKey($"RETIRE-FINGERPRINT-{Guid.NewGuid():N}");
        var retiredFingerprintKeyId =
            $"game-fingerprint-retired-{Guid.NewGuid():N}";
        var rotationProvider = CreateRotationProvider(
            previousFingerprintKeyId: retiredFingerprintKeyId);
        var protection = new GameKeyProtectionService(rotationProvider);
        var databaseCreated = false;

        try
        {
            await ExecuteAdminCommandAsync(
                $"CREATE DATABASE \"{database}\";",
                cancellationToken);
            databaseCreated = true;

            var ownerOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_fixture.CreateOwnerConnectionString(database))
                .Options;
            await using (var migrationDb = new AppDbContext(ownerOptions))
            {
                await migrationDb.Database.MigrateAsync(cancellationToken);
            }

            long gameKeyId;
            long targetDropId;
            long targetOwnerId;
            await using (var setupDb = new AppDbContext(ownerOptions))
            {
                var sourceDrop = await CreateDropAsync(setupDb);
                var gameKey = AddProtectedGameKey(
                    setupDb,
                    sourceDrop,
                    protection,
                    value);
                var targetDrop = await CreateDropAsync(setupDb);
                AddProtectedGameKey(
                    setupDb,
                    targetDrop,
                    protection,
                    ParseKey($"RETIRE-TARGET-{Guid.NewGuid():N}"));
                await setupDb.SaveChangesAsync(cancellationToken);
                gameKeyId = gameKey.Id;
                targetDropId = targetDrop.Id;
                targetOwnerId = targetDrop.OwnerId;
            }

            var retirementProvider = CreateRotationProvider(
                includePreviousFingerprint: false,
                previousFingerprintKeyId: retiredFingerprintKeyId);
            int deletedCount;
            await using (var retirementDb = new AppDbContext(ownerOptions))
            {
                var service = new GameKeyMigrationService(
                    retirementDb,
                    retirementProvider);
                deletedCount = await service.RetireFingerprintKeyAsync(
                    retiredFingerprintKeyId,
                    batchSize: 1,
                    cancellationToken);
            }

            AddKeyInventoryResult duplicateResult;
            await using (var inventoryDb =
                         _fixture.NewApplicationDbContext(database))
            {
                duplicateResult = await new KeyDropInventoryService(
                        inventoryDb,
                        new GameKeyRegistrationWriter(
                            inventoryDb,
                            new GameKeyProtectionService(retirementProvider)),
                        new KeyDropMutationLock(inventoryDb),
                        NullWaitingRoomLifecycle.Instance)
                    .AddAsync(
                        targetDropId,
                        targetOwnerId,
                        value.Value,
                        cancellationToken);
            }

            await using var verificationDb = new AppDbContext(ownerOptions);
            var remainingFingerprintKeyIds = await verificationDb
                .GameKeyFingerprints
                .Where(fingerprint => fingerprint.GameKeyId == gameKeyId)
                .Select(fingerprint => fingerprint.KeyId)
                .ToListAsync(cancellationToken);
            var targetTotalKeys = await verificationDb.KeyDrops
                .Where(drop => drop.Id == targetDropId)
                .Select(drop => drop.TotalKeys)
                .SingleAsync(cancellationToken);

            Assert.Equal(2, deletedCount);
            Assert.Equal(
                ["game-fingerprint-test-v2"],
                remainingFingerprintKeyIds);
            Assert.Equal(
                AddKeyInventoryOutcome.Invalid,
                duplicateResult.Outcome);
            Assert.Equal(2, targetTotalKeys);
        }
        finally
        {
            if (databaseCreated)
            {
                await ExecuteAdminCommandAsync(
                    $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);",
                    CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task MigrateBatchAsync_DuplicateAcrossBatchesStopsWithoutExposingValue()
    {
        var duplicateValue = $"DUPLICATE-SECRET-{Guid.NewGuid():N}";
        long firstGameKeyId;
        long secondGameKeyId;
        var provider = CreateRotationProvider();
        var protection = new GameKeyProtectionService(provider);

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var drop = await CreateDropAsync(setupDb);
            var first = NewEncryptedKey(drop, protection, duplicateValue);
            var second = NewEncryptedKey(drop, protection, duplicateValue);
            setupDb.GameKeys.AddRange(first, second);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstGameKeyId = first.Id;
            secondGameKeyId = second.Id;
        }

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

        Assert.All(
            storedKeys,
            gameKey => Assert.Equal(
                "game-encryption-test-v2",
                gameKey.EncryptionKeyId));
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

    private static IKeyProvider CreateRotationProvider(
        bool includePreviousFingerprint = true,
        string previousFingerprintKeyId = "game-fingerprint-test-v1")
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
            previousFingerprintKeyId;
        values["AccountProtection:Purposes:game-key-fingerprint:ReadKeyIds:1"] =
            "game-fingerprint-test-v2";
        values["AccountProtection:Purposes:game-key-fingerprint:WriteKeyIds:0"] =
            previousFingerprintKeyId;
        values["AccountProtection:Purposes:game-key-fingerprint:WriteKeyIds:1"] =
            "game-fingerprint-test-v2";
        values[
            "AccountProtection:Purposes:game-key-fingerprint:Keys:game-fingerprint-test-v2"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)52, 32).ToArray());
        if (!string.Equals(
                previousFingerprintKeyId,
                "game-fingerprint-test-v1",
                StringComparison.Ordinal))
        {
            values[
                $"AccountProtection:Purposes:game-key-fingerprint:Keys:{previousFingerprintKeyId}"] =
                Convert.ToBase64String(Enumerable.Repeat((byte)53, 32).ToArray());
        }

        if (!includePreviousFingerprint)
        {
            values["AccountProtection:Purposes:game-key-fingerprint:ReadKeyIds:0"] =
                "game-fingerprint-test-v2";
            values.Remove(
                "AccountProtection:Purposes:game-key-fingerprint:ReadKeyIds:1");
            values["AccountProtection:Purposes:game-key-fingerprint:WriteKeyIds:0"] =
                "game-fingerprint-test-v2";
            values.Remove(
                "AccountProtection:Purposes:game-key-fingerprint:WriteKeyIds:1");
        }

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

    private static GameKey NewEncryptedKey(
        KeyDrop drop,
        GameKeyProtectionService protection,
        string value)
    {
        var protectedKey = protection.Protect(ParseKey(value));
        return new GameKey
        {
            DropId = drop.Id,
            Ciphertext = protectedKey.Ciphertext,
            Nonce = protectedKey.Nonce,
            AuthenticationTag = protectedKey.AuthenticationTag,
            EncryptionKeyId = protectedKey.EncryptionKeyId,
        };
    }

    private static GameKey AddProtectedGameKey(
        AppDbContext db,
        KeyDrop drop,
        GameKeyProtectionService protection,
        GameKeyValue value)
    {
        var protectedKey = protection.Protect(value);
        var gameKey = new GameKey
        {
            DropId = drop.Id,
            Ciphertext = protectedKey.Ciphertext,
            Nonce = protectedKey.Nonce,
            AuthenticationTag = protectedKey.AuthenticationTag,
            EncryptionKeyId = protectedKey.EncryptionKeyId,
        };
        db.GameKeys.Add(gameKey);
        foreach (var fingerprint in protection.CreateWritableFingerprints(value))
        {
            db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
            {
                GameKey = gameKey,
                KeyId = fingerprint.KeyId,
                Value = fingerprint.Value,
            });
        }

        return gameKey;
    }

    private async Task ExecuteAdminCommandAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(
            _fixture.CreateOwnerConnectionString("postgres"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
