using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameClassificationConstraintTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameClassificationConstraintTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CatalogConstraints_RejectInvalidAndDuplicateValues()
    {
        var invalidCode = await InsertTagExpectingFailureAsync(
            -1001,
            "Not-Kebab",
            "유효한 이름 1",
            10);
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidCode.SqlState);
        Assert.Equal("CK_game_tags_code_format", invalidCode.ConstraintName);

        var invalidDisplayName = await InsertTagExpectingFailureAsync(
            -1002,
            "valid-code-1",
            " 앞뒤 공백",
            10);
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidDisplayName.SqlState);
        Assert.Equal("CK_game_tags_display_name_format", invalidDisplayName.ConstraintName);

        var invalidSortOrder = await InsertTagExpectingFailureAsync(
            -1003,
            "valid-code-2",
            "유효한 이름 2",
            GameClassificationPolicy.MaxSortOrder + 1);
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidSortOrder.SqlState);
        Assert.Equal("CK_game_tags_sort_order_range", invalidSortOrder.ConstraintName);

        var duplicateCode = await InsertTagExpectingFailureAsync(
            -1004,
            "single-player",
            "유효한 이름 3",
            10);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateCode.SqlState);
        Assert.Equal("IX_game_tags_code", duplicateCode.ConstraintName);

        var duplicateDisplayName = await InsertTagExpectingFailureAsync(
            -1005,
            "valid-code-3",
            "싱글플레이",
            10);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateDisplayName.SqlState);
        Assert.Equal("IX_game_tags_display_name", duplicateDisplayName.ConstraintName);
    }

    [Fact]
    public async Task Catalogs_HaveUniqueAndActiveSortIndexes()
    {
        await using var db = _fixture.NewOwnerDbContext();
        var indexNames = await db.Database.SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value"
                  FROM pg_indexes
                 WHERE schemaname = 'public'
                   AND tablename IN ('game_genres', 'game_tags', 'game_platforms')
                """)
            .ToListAsync(TestContext.Current.CancellationToken);

        AssertContainsCatalogIndexes(indexNames, "genres");
        AssertContainsCatalogIndexes(indexNames, "tags");
        AssertContainsCatalogIndexes(indexNames, "platforms");
    }

    [Fact]
    public async Task ApplicationRole_CanReadCatalogsButCannotMutateThem()
    {
        await using (var readDb = _fixture.NewDbContext())
        {
            Assert.NotEmpty(await readDb.GameGenres
                .AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken));
            Assert.NotEmpty(await readDb.GameTags
                .AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken));
            Assert.NotEmpty(await readDb.GamePlatforms
                .AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken));
        }

        await AssertCatalogMutationDeniedAsync(
            """
            INSERT INTO game_tags (id, code, display_name, sort_order, is_active)
            VALUES (-2001, 'runtime-created', '런타임 생성', 10, TRUE)
            """);
        await AssertCatalogMutationDeniedAsync(
            "UPDATE game_tags SET display_name = '변조' WHERE id = 10");
        await AssertCatalogMutationDeniedAsync(
            "DELETE FROM game_tags WHERE id = 10");
    }

    [Fact]
    public async Task ApplicationRole_CanAddAndRemoveAssignmentsButCannotUpdateThem()
    {
        var gameId = await CreateGameAsync();
        await using (var insertDb = _fixture.NewDbContext())
        {
            insertDb.GameTagAssignments.Add(new GameTagAssignment
            {
                GameId = gameId,
                TagId = 10,
            });
            await insertDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var updateDb = _fixture.NewDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                updateDb.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE game_tag_assignments SET tag_id = {11L} WHERE game_id = {gameId} AND tag_id = {10L}",
                    TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }

        await using (var deleteDb = _fixture.NewDbContext())
        {
            var deleted = await deleteDb.GameTagAssignments
                .Where(item => item.GameId == gameId && item.TagId == 10)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, deleted);
        }
    }

    [Fact]
    public async Task DuplicateAssignments_AreRejected()
    {
        var gameId = await CreateGameAsync();
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.GameTagAssignments.Add(new GameTagAssignment
            {
                GameId = gameId,
                TagId = 10,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicateDb = _fixture.NewDbContext();
        duplicateDb.GameTagAssignments.Add(new GameTagAssignment
        {
            GameId = gameId,
            TagId = 10,
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicateDb.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task TagAssignments_CannotExceedPerGameLimit()
    {
        var gameId = await CreateGameAsync();
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.GameTagAssignments.AddRange(
                Enumerable.Range(10, GameClassificationPolicy.MaxTagsPerGame)
                    .Select(tagId => new GameTagAssignment
                    {
                        GameId = gameId,
                        TagId = tagId,
                    }));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var attemptDb = _fixture.NewDbContext();
        attemptDb.GameTagAssignments.Add(new GameTagAssignment
        {
            GameId = gameId,
            TagId = 18,
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => attemptDb.SaveChangesAsync(TestContext.Current.CancellationToken));

        AssertLimitViolation(exception, "CK_game_tag_assignments_max_per_game");
    }

    [Fact]
    public async Task PlatformAssignments_CannotExceedPerGameLimit()
    {
        var gameId = await CreateGameAsync();
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.GamePlatformAssignments.AddRange(
                Enumerable.Range(10, GameClassificationPolicy.MaxPlatformsPerGame)
                    .Select(platformId => new GamePlatformAssignment
                    {
                        GameId = gameId,
                        PlatformId = platformId,
                    }));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var attemptDb = _fixture.NewDbContext();
        attemptDb.GamePlatformAssignments.Add(new GamePlatformAssignment
        {
            GameId = gameId,
            PlatformId = GameClassificationCatalog.UnspecifiedPlatformId,
        });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => attemptDb.SaveChangesAsync(TestContext.Current.CancellationToken));

        AssertLimitViolation(exception, "CK_game_platform_assignments_max_per_game");
    }

    [Fact]
    public async Task ConcurrentTagInserts_KeepThePerGameLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gameId = await CreateGameAsync();
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.GameTagAssignments.AddRange(
                Enumerable.Range(10, GameClassificationPolicy.MaxTagsPerGame - 1)
                    .Select(tagId => new GameTagAssignment
                    {
                        GameId = gameId,
                        TagId = tagId,
                    }));
            await arrangeDb.SaveChangesAsync(cancellationToken);
        }

        await using var firstDb = _fixture.NewDbContext();
        await using var firstTransaction = await firstDb.Database.BeginTransactionAsync(
            cancellationToken);
        firstDb.GameTagAssignments.Add(new GameTagAssignment
        {
            GameId = gameId,
            TagId = 17,
        });
        await firstDb.SaveChangesAsync(cancellationToken);

        await using var secondDb = _fixture.NewDbContext();
        secondDb.GameTagAssignments.Add(new GameTagAssignment
        {
            GameId = gameId,
            TagId = 18,
        });
        var competingSave = secondDb.SaveChangesAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);
        Assert.False(competingSave.IsCompleted);

        await firstTransaction.CommitAsync(cancellationToken);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await competingSave);
        AssertLimitViolation(exception, "CK_game_tag_assignments_max_per_game");
    }

    private async Task<PostgresException> InsertTagExpectingFailureAsync(
        long id,
        string code,
        string displayName,
        int sortOrder)
    {
        await using var db = _fixture.NewOwnerDbContext();
        return await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO game_tags (id, code, display_name, sort_order, is_active) VALUES ({id}, {code}, {displayName}, {sortOrder}, TRUE)",
                TestContext.Current.CancellationToken));
    }

    private async Task AssertCatalogMutationDeniedAsync(string sql)
    {
        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                sql,
                TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private async Task<long> CreateGameAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        return game.Id;
    }

    private static void AssertContainsCatalogIndexes(
        IReadOnlyCollection<string> indexNames,
        string catalogName)
    {
        Assert.Contains($"IX_game_{catalogName}_code", indexNames);
        Assert.Contains($"IX_game_{catalogName}_display_name", indexNames);
        Assert.Contains($"IX_game_{catalogName}_active_sort", indexNames);
    }

    private static void AssertLimitViolation(
        DbUpdateException exception,
        string constraintName)
    {
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(constraintName, postgresException.ConstraintName);
    }
}
