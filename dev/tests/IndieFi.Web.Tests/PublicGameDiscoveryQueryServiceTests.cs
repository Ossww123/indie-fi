using IndieFi.Web.Data;
using IndieFi.Web.Modules.Games.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PublicGameDiscoveryQueryServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public PublicGameDiscoveryQueryServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Query_CombinesSearchGenreAllTagsAnyPlatformAndTimeRange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var firstToken = $"star{suffix}";
        var secondToken = $"quest{suffix}";
        await using var ownerDb = _fixture.NewOwnerDbContext();
        var ownerId = await AddOwnerAsync(ownerDb, suffix, cancellationToken);
        var expectedId = await AddGameAsync(
            ownerDb,
            ownerId,
            "action",
            $"{firstToken} adventure",
            $"A long {secondToken} journey",
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            ["cooperative", "pixel-art"],
            ["linux"],
            cancellationToken);
        await AddGameAsync(
            ownerDb,
            ownerId,
            "action",
            $"{firstToken} missing tag",
            secondToken,
            new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
            ["cooperative"],
            ["linux"],
            cancellationToken);
        await AddGameAsync(
            ownerDb,
            ownerId,
            "action",
            $"{firstToken} wrong platform",
            secondToken,
            new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero),
            ["cooperative", "pixel-art"],
            ["macos"],
            cancellationToken);
        await AddGameAsync(
            ownerDb,
            ownerId,
            "adventure",
            $"{firstToken} wrong genre",
            secondToken,
            new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
            ["cooperative", "pixel-art"],
            ["linux"],
            cancellationToken);
        await AddGameAsync(
            ownerDb,
            ownerId,
            "action",
            $"{firstToken} outside range",
            secondToken,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            ["cooperative", "pixel-art"],
            ["linux"],
            cancellationToken);

        await using var db = _fixture.NewDbContext();
        var service = CreateService(db);
        var filter = CreateFilter(
            $"{firstToken} {secondToken}",
            genre: "action",
            tags: ["cooperative", "pixel-art"],
            platforms: ["windows", "linux"],
            createdFrom: new DateTimeOffset(
                2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            createdBefore: new DateTimeOffset(
                2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        var result = await service.QueryAsync(
            filter,
            cancellationToken: cancellationToken);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
        Assert.Null(result.CursorFailure);
        var item = Assert.Single(result.Page!.Items);
        Assert.Equal(expectedId, item.Id);
        Assert.Null(result.Page.NextCursor);
    }

    [Fact]
    public async Task Query_WithKeysetCursor_ReturnsStablePagesAfterNewInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var token = $"page{suffix}";
        var createdAt = new DateTimeOffset(
            2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        await using var ownerDb = _fixture.NewOwnerDbContext();
        var ownerId = await AddOwnerAsync(ownerDb, suffix, cancellationToken);
        var originalIds = new List<long>();
        for (var index = 0; index < 26; index++)
        {
            originalIds.Add(await AddGameAsync(
                ownerDb,
                ownerId,
                "action",
                $"{token} {index:D2}",
                null,
                createdAt,
                ["cooperative"],
                ["windows"],
                cancellationToken));
        }

        await using var db = _fixture.NewDbContext();
        var service = CreateService(db);
        var filter = CreateFilter(token);
        var first = await service.QueryAsync(
            filter,
            cancellationToken: cancellationToken);

        Assert.True(first.IsValid);
        Assert.Equal(PublicGameDiscoveryQueryService.PageSize, first.Page!.Items.Count);
        Assert.NotNull(first.Page.NextCursor);

        var newId = await AddGameAsync(
            ownerDb,
            ownerId,
            "action",
            $"{token} newly inserted",
            null,
            createdAt.AddDays(1),
            ["cooperative"],
            ["windows"],
            cancellationToken);
        var second = await service.QueryAsync(
            filter,
            first.Page.NextCursor,
            cancellationToken);

        Assert.True(second.IsValid);
        Assert.Equal(2, second.Page!.Items.Count);
        Assert.Null(second.Page.NextCursor);
        var combinedIds = first.Page.Items
            .Concat(second.Page.Items)
            .Select(item => item.Id)
            .ToArray();
        Assert.Equal(originalIds.OrderDescending(), combinedIds);
        Assert.DoesNotContain(newId, combinedIds);
        Assert.Equal(combinedIds.Length, combinedIds.Distinct().Count());
    }

    [Fact]
    public async Task Query_WithUnknownOrInactiveCatalogCodes_ReturnsEveryViolation()
    {
        await using var db = _fixture.NewDbContext();
        var service = CreateService(db);
        var filter = CreateFilter(
            query: null,
            genre: "unspecified",
            tags: ["unknown-tag"],
            platforms: ["unspecified"]);

        var result = await service.QueryAsync(
            filter,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Null(result.Page);
        Assert.Contains(
            result.Violations,
            violation => violation.Field
                == PublicGameDiscoveryInputValidator.GenreField);
        Assert.Contains(
            result.Violations,
            violation => violation.Field
                == PublicGameDiscoveryInputValidator.TagsField);
        Assert.Contains(
            result.Violations,
            violation => violation.Field
                == PublicGameDiscoveryInputValidator.PlatformsField);
    }

    [Fact]
    public async Task Query_WithCursorForDifferentFilter_ReturnsCursorViolation()
    {
        await using var db = _fixture.NewDbContext();
        var codec = CreateCodec();
        var original = CreateFilter("galaxy");
        var cursor = codec.Encode(
            new PublicGameDiscoveryCursorBoundary(Now, 1042),
            original);
        var service = new PublicGameDiscoveryQueryService(db, codec);

        var result = await service.QueryAsync(
            CreateFilter("planet"),
            cursor,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(
            PublicGameDiscoveryCursorDecodeStatus.FilterMismatch,
            result.CursorFailure);
        Assert.Contains(
            result.Violations,
            violation => violation.Field
                == PublicGameDiscoveryQueryService.CursorField);
    }

    private PublicGameDiscoveryQueryService CreateService(AppDbContext db) =>
        new(db, CreateCodec());

    private static PublicGameDiscoveryCursorCodec CreateCodec() =>
        new(
            new EphemeralDataProtectionProvider(),
            new FixedTimeProvider(Now));

    private static PublicGameDiscoveryFilter CreateFilter(
        string? query,
        string? genre = "action",
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? platforms = null,
        DateTimeOffset? createdFrom = null,
        DateTimeOffset? createdBefore = null)
    {
        var result = PublicGameDiscoveryInputValidator.Validate(new(
            query,
            genre,
            tags ?? ["cooperative"],
            platforms ?? ["windows"],
            createdFrom?.ToString("O"),
            createdBefore?.ToString("O")));
        Assert.True(result.IsValid);
        return result.Filter!;
    }

    private static async Task<long> AddOwnerAsync(
        AppDbContext db,
        string suffix,
        CancellationToken cancellationToken)
    {
        var owner = TestAccountProtection.AddUser(
            db,
            $"discovery-{suffix}@example.test");
        await db.SaveChangesAsync(cancellationToken);
        return owner.Id;
    }

    private static async Task<long> AddGameAsync(
        AppDbContext db,
        long ownerId,
        string genreCode,
        string title,
        string? description,
        DateTimeOffset createdAt,
        IReadOnlyList<string> tagCodes,
        IReadOnlyList<string> platformCodes,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var insertGame = new NpgsqlCommand(
            """
            INSERT INTO games (owner_id, genre_id, title, description, created_at)
            SELECT $1, id, $2, $3, $4
              FROM game_genres
             WHERE code = $5
            RETURNING id
            """,
            connection);
        insertGame.Parameters.AddWithValue(ownerId);
        insertGame.Parameters.AddWithValue(title);
        insertGame.Parameters.AddWithValue((object?)description ?? DBNull.Value);
        insertGame.Parameters.AddWithValue(createdAt);
        insertGame.Parameters.AddWithValue(genreCode);
        var gameId = (long)(await insertGame.ExecuteScalarAsync(cancellationToken))!;

        await AddAssignmentsAsync(
            connection,
            "game_tag_assignments",
            "tag_id",
            "game_tags",
            gameId,
            tagCodes,
            cancellationToken);
        await AddAssignmentsAsync(
            connection,
            "game_platform_assignments",
            "platform_id",
            "game_platforms",
            gameId,
            platformCodes,
            cancellationToken);
        return gameId;
    }

    private static async Task AddAssignmentsAsync(
        NpgsqlConnection connection,
        string assignmentTable,
        string assignmentColumn,
        string catalogTable,
        long gameId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {assignmentTable} (game_id, {assignmentColumn})
            SELECT $1, id
              FROM {catalogTable}
             WHERE code = ANY ($2)
            """,
            connection);
        command.Parameters.AddWithValue(gameId);
        command.Parameters.AddWithValue(codes.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
