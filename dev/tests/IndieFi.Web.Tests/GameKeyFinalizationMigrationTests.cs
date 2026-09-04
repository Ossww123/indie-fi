using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameKeyFinalizationMigrationTests
{
    private const string TransitionMigration =
        "20260815125900_ExpandEncryptedGameKeyInventory";

    private readonly PostgreSqlFixture _fixture;

    public GameKeyFinalizationMigrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FinalMigration_RollsBackUntilPlaintextBackfillIsComplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var database = $"game_key_finalize_{suffix}";
        var secret = $"UNMIGRATED-SECRET-{suffix}";
        var databaseCreated = false;

        try
        {
            await ExecuteAdminCommandAsync(
                $"CREATE DATABASE \"{database}\";",
                cancellationToken);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_fixture.CreateOwnerConnectionString(database))
                .Options;
            await using var db = new AppDbContext(options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync(TransitionMigration, cancellationToken);

            var owner = TestAccountProtection.AddUser(
                db,
                $"{suffix}@example.test");
            await db.SaveChangesAsync(cancellationToken);
            // This database intentionally stops before later game and key-drop columns
            // exist, so seed them through that migration's schema rather than today's model.
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var insertGame = new NpgsqlCommand(
                """
                INSERT INTO games (owner_id, title)
                VALUES ($1, $2)
                RETURNING id
                """,
                (NpgsqlConnection)db.Database.GetDbConnection());
            insertGame.Parameters.AddWithValue(owner.Id);
            insertGame.Parameters.AddWithValue($"Migration game {suffix}");
            var gameId = (long)(await insertGame.ExecuteScalarAsync(cancellationToken))!;
            var opensAt = DateTimeOffset.UtcNow.AddHours(1);
            await using var insertDrop = new NpgsqlCommand(
                """
                INSERT INTO key_drops (game_id, owner_id, opens_at, total_keys)
                VALUES ($1, $2, $3, 1)
                RETURNING id
                """,
                (NpgsqlConnection)db.Database.GetDbConnection());
            insertDrop.Parameters.AddWithValue(gameId);
            insertDrop.Parameters.AddWithValue(owner.Id);
            insertDrop.Parameters.AddWithValue(opensAt);
            var dropId = (long)(await insertDrop.ExecuteScalarAsync(cancellationToken))!;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO game_keys (drop_id, key_value) VALUES ({dropId}, {secret})",
                cancellationToken);
            var gameKeyId = await db.Database
                .SqlQueryRaw<long>(
                    """
                    SELECT id AS "Value"
                    FROM game_keys
                    ORDER BY id DESC
                    LIMIT 1
                    """)
                .SingleAsync(cancellationToken);

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                migrator.MigrateAsync(cancellationToken: cancellationToken));

            Assert.Contains(
                "backfill is incomplete",
                exception.MessageText,
                StringComparison.Ordinal);
            Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);

            var columns = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT column_name AS "Value"
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'game_keys'
                    """)
                .ToListAsync(cancellationToken);
            Assert.Contains("key_value", columns);
            Assert.DoesNotContain("20260815135805_FinalizeGameKeyProtection",
                await db.Database.GetAppliedMigrationsAsync(cancellationToken));

            var parsed = GameKeyPolicy.ParseBatch(secret);
            var value = Assert.Single(parsed.Keys);
            var protection = TestAccountProtection.CreateGameKeyProtectionService();
            var protectedKey = protection.Protect(value);
            var fingerprint = Assert.Single(
                protection.CreateWritableFingerprints(value));
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE game_keys
                SET ciphertext = {protectedKey.Ciphertext},
                    nonce = {protectedKey.Nonce},
                    authentication_tag = {protectedKey.AuthenticationTag},
                    encryption_key_id = {protectedKey.EncryptionKeyId}
                WHERE id = {gameKeyId}
                """,
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO game_key_fingerprints (game_key_id, key_id, value)
                VALUES ({gameKeyId}, {fingerprint.KeyId}, {fingerprint.Value})
                """,
                cancellationToken);

            await migrator.MigrateAsync(cancellationToken: cancellationToken);

            var finalColumns = await db.Database
                .SqlQueryRaw<string>(
                    """
                    SELECT column_name AS "Value"
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'game_keys'
                    """)
                .ToListAsync(cancellationToken);
            Assert.DoesNotContain("key_value", finalColumns);
            Assert.Contains("20260815135805_FinalizeGameKeyProtection",
                await db.Database.GetAppliedMigrationsAsync(cancellationToken));

            db.ChangeTracker.Clear();
            var storedKey = await db.GameKeys.SingleAsync(
                gameKey => gameKey.Id == gameKeyId,
                cancellationToken);
            var restoredValue = protection.Unprotect(new ProtectedGameKey(
                storedKey.Ciphertext,
                storedKey.Nonce,
                storedKey.AuthenticationTag,
                storedKey.EncryptionKeyId));
            Assert.Equal(secret, restoredValue.Value);
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
}
