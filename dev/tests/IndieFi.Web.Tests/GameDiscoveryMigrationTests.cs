using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameDiscoveryMigrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameDiscoveryMigrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LatestMigration_ProvidesSearchAndKeysetIndexesToApplicationRole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = _fixture.NewDbContext();
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        await using (var extensionCommand = new NpgsqlCommand(
            "SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'",
            connection))
        {
            Assert.Equal(
                1L,
                (long)(await extensionCommand.ExecuteScalarAsync(cancellationToken))!);
        }

        await using (var plannerCommand = new NpgsqlCommand(
            "SET enable_seqscan = off",
            connection))
        {
            await plannerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var searchPlan = await ReadPlanAsync(
            connection,
            """
            EXPLAIN (COSTS OFF)
            SELECT id
              FROM games
             WHERE lower(title || ' ' || coalesce(description, ''))
                   LIKE '%galaxy%' ESCAPE '\'
             ORDER BY created_at DESC, id DESC
             LIMIT 25
            """,
            cancellationToken);
        var latestPlan = await ReadPlanAsync(
            connection,
            """
            EXPLAIN (COSTS OFF)
            SELECT id
              FROM games
             ORDER BY created_at DESC, id DESC
             LIMIT 25
            """,
            cancellationToken);

        Assert.Contains("IX_games_search_document_trgm", searchPlan);
        Assert.Contains("IX_games_created_at_id_desc", latestPlan);
    }

    private static async Task<string> ReadPlanAsync(
        NpgsqlConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(commandText, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var lines = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
