using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlBackupRestoreTests
{
    private readonly PostgreSqlFixture _fixture;

    public PostgreSqlBackupRestoreTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CustomDump_RestoresSchemaEncryptedAccountAndApplicationPermissions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"backup-restore-{suffix}@example.test";
        var restoredDatabase = $"restore_{suffix}";
        var dumpPath = $"/tmp/indiefi-{suffix}.dump";
        byte[] expectedCiphertext;
        var databaseCreated = false;

        await using (var sourceDb = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(sourceDb, email);
            await sourceDb.SaveChangesAsync(cancellationToken);
            expectedCiphertext = user.EmailCiphertext.ToArray();
        }

        try
        {
            AssertSucceeded(await _fixture.ExecAsync(
                [
                    "pg_dump",
                    "--username=postgres",
                    "--dbname=indiefi_tests",
                    "--format=custom",
                    "--no-owner",
                    $"--file={dumpPath}",
                ],
                cancellationToken));

            await ExecuteAdminCommandAsync(
                $"CREATE DATABASE \"{restoredDatabase}\";",
                cancellationToken);
            databaseCreated = true;

            AssertSucceeded(await _fixture.ExecAsync(
                [
                    "pg_restore",
                    "--username=postgres",
                    $"--dbname={restoredDatabase}",
                    "--exit-on-error",
                    "--no-owner",
                    dumpPath,
                ],
                cancellationToken));

            // Use the restricted application role, not the database owner. This also
            // proves that the dump preserved the grants required by the running app.
            await using var restoredDb =
                _fixture.NewApplicationDbContext(restoredDatabase);
            var restoredUser = await TestAccountProtection.FindUserAsync(
                restoredDb,
                email,
                cancellationToken);

            Assert.Equal(expectedCiphertext, restoredUser.EmailCiphertext);
            Assert.True(await restoredDb.Database.CanConnectAsync(cancellationToken));
        }
        finally
        {
            if (databaseCreated)
            {
                await ExecuteAdminCommandAsync(
                    $"DROP DATABASE IF EXISTS \"{restoredDatabase}\" WITH (FORCE);",
                    CancellationToken.None);
            }

            AssertSucceeded(await _fixture.ExecAsync(
                ["rm", "-f", dumpPath],
                CancellationToken.None));
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

    private static void AssertSucceeded(ExecResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Container command failed with exit code {result.ExitCode}: {result.Stderr}");
    }
}
