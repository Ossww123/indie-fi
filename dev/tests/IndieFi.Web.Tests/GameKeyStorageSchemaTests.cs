using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameKeyStorageSchemaTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameKeyStorageSchemaTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Database_RejectsGrantWhoseGameKeyBelongsToAnotherDrop()
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        var recipient = TestAccountProtection.AddUser(
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

        var firstDrop = NewDrop(game, owner);
        var secondDrop = NewDrop(game, owner);
        db.KeyDrops.AddRange(firstDrop, secondDrop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var gameKey = TestAccountProtection.AddGameKey(
            db,
            secondDrop,
            $"KEY-{Guid.NewGuid():N}");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.KeyGrants.Add(new KeyGrant
        {
            DropId = firstDrop.Id,
            UserId = recipient.Id,
            KeyId = gameKey.Id,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
        Assert.Equal(
            "FK_key_grants_game_keys_key_id_drop_id",
            postgresException.ConstraintName);
    }

    [Fact]
    public async Task Database_StoresEncryptedRowsAndHasNoPlaintextColumn()
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(db);
        var encryptedKey = TestAccountProtection.AddGameKey(
            db,
            drop,
            $"ENCRYPTED-{Guid.NewGuid():N}");

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var storedEncryptedKey = await db.GameKeys.SingleAsync(
            key => key.Id == encryptedKey.Id,
            TestContext.Current.CancellationToken);
        var storedFingerprint = await db.GameKeyFingerprints.SingleAsync(
            item => item.GameKeyId == encryptedKey.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(storedEncryptedKey.Ciphertext);
        Assert.Equal(GameKeyProtectionService.NonceSize, storedEncryptedKey.Nonce.Length);
        Assert.Equal(
            GameKeyProtectionService.AuthenticationTagSize,
            storedEncryptedKey.AuthenticationTag.Length);
        Assert.Equal(GameKeyProtectionService.FingerprintSize, storedFingerprint.Value.Length);

        var columns = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT column_name AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'game_keys'
                """)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("key_value", columns);
        Assert.Contains("ciphertext", columns);
    }

    [Fact]
    public async Task Database_RejectsIncompleteEncryptionMetadata()
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(db);
        db.GameKeys.Add(new GameKey
        {
            DropId = drop.Id,
            Ciphertext = [1],
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal("CK_game_keys_encryption_shape", postgresException.ConstraintName);
    }

    [Fact]
    public async Task Database_RejectsGameKeyWithoutEncryptedMaterial()
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(db);
        db.GameKeys.Add(new GameKey
        {
            DropId = drop.Id,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal("CK_game_keys_encryption_shape", postgresException.ConstraintName);
    }

    [Theory]
    [InlineData(0, 12, 16, "game-encryption-test-v1")]
    [InlineData(1, 11, 16, "game-encryption-test-v1")]
    [InlineData(1, 12, 15, "game-encryption-test-v1")]
    [InlineData(1, 12, 16, " ")]
    public async Task Database_RejectsInvalidEncryptionMetadataLengths(
        int ciphertextLength,
        int nonceLength,
        int authenticationTagLength,
        string encryptionKeyId)
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(db);
        db.GameKeys.Add(new GameKey
        {
            DropId = drop.Id,
            Ciphertext = new byte[ciphertextLength],
            Nonce = new byte[nonceLength],
            AuthenticationTag = new byte[authenticationTagLength],
            EncryptionKeyId = encryptionKeyId,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal("CK_game_keys_encryption_shape", postgresException.ConstraintName);
    }

    [Fact]
    public async Task Database_RejectsDuplicateFingerprintAcrossGameKeys()
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(db);
        var firstKey = NewEncryptedKey(drop);
        var secondKey = NewEncryptedKey(drop);
        db.GameKeys.AddRange(firstKey, secondKey);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var duplicateValue = Enumerable.Repeat((byte)42, 32).ToArray();
        db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
        {
            GameKeyId = firstKey.Id,
            KeyId = "game-fingerprint-test-v1",
            Value = duplicateValue,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
        {
            GameKeyId = secondKey.Id,
            KeyId = "game-fingerprint-test-v1",
            Value = duplicateValue,
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(
            "IX_game_key_fingerprints_key_id_value",
            postgresException.ConstraintName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task Database_RejectsInvalidFingerprintLength(int valueLength)
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(db);
        var gameKey = NewEncryptedKey(drop);
        db.GameKeys.Add(gameKey);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
        {
            GameKeyId = gameKey.Id,
            KeyId = "game-fingerprint-test-v1",
            Value = new byte[valueLength],
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "CK_game_key_fingerprints_value_length",
            postgresException.ConstraintName);
    }

    [Fact]
    public async Task Database_AllowsSameFingerprintBytesForDifferentVersions()
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(db);
        var gameKey = NewEncryptedKey(drop);
        db.GameKeys.Add(gameKey);
        var sharedValue = Enumerable.Repeat((byte)43, 32).ToArray();
        db.GameKeyFingerprints.AddRange(
            new StoredGameKeyFingerprint
            {
                GameKey = gameKey,
                KeyId = "game-fingerprint-test-v1",
                Value = sharedValue,
            },
            new StoredGameKeyFingerprint
            {
                GameKey = gameKey,
                KeyId = "game-fingerprint-test-v2",
                Value = sharedValue,
            });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            2,
            await db.GameKeyFingerprints.CountAsync(
                item => item.GameKeyId == gameKey.Id,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplicationRole_CannotUpdateOrDeleteGameKeyInventory()
    {
        long gameKeyId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var drop = await CreateDropAsync(arrangeDb);
            var gameKey = NewEncryptedKey(drop);
            arrangeDb.GameKeys.Add(gameKey);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameKeyId = gameKey.Id;
        }

        await using var updateDb = _fixture.NewDbContext();
        var replacementCiphertext = new byte[] { 1 };
        var updateException = await Assert.ThrowsAsync<PostgresException>(() =>
            updateDb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE game_keys SET ciphertext = {replacementCiphertext} WHERE id = {gameKeyId}",
                TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateException.SqlState);

        await using var deleteDb = _fixture.NewDbContext();
        var deleteException = await Assert.ThrowsAsync<PostgresException>(() =>
            deleteDb.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM game_keys WHERE id = {gameKeyId}",
                TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteException.SqlState);
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
        var drop = NewDrop(game, owner);
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return drop;
    }

    private static GameKey NewEncryptedKey(KeyDrop drop)
    {
        var value = ParseKey($"KEY-{Guid.NewGuid():N}");
        var protectedKey = TestAccountProtection
            .CreateGameKeyProtectionService()
            .Protect(value);
        return new GameKey
        {
            DropId = drop.Id,
            Ciphertext = protectedKey.Ciphertext,
            Nonce = protectedKey.Nonce,
            AuthenticationTag = protectedKey.AuthenticationTag,
            EncryptionKeyId = protectedKey.EncryptionKeyId,
        };
    }

    private static GameKeyValue ParseKey(string value)
    {
        var result = GameKeyPolicy.ParseBatch(value);
        Assert.True(result.IsValid);
        return Assert.Single(result.Keys);
    }

    private static KeyDrop NewDrop(Game game, User owner) => new()
    {
        GameId = game.Id,
        OwnerId = owner.Id,
        OpensAt = DateTimeOffset.UtcNow.AddHours(1),
        TotalKeys = 1,
    };
}
