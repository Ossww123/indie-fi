using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.Games.Domain;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PublicGameDetailQueryServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public PublicGameDetailQueryServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GameDetailReadPath_ReturnsCompleteDetailInFourDatabaseCommands()
    {
        long gameId;
        long viewerUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 2);
            var viewer = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0);
            viewerUserId = viewer.Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            game.GenreId = 12;
            game.Description = "Measured detail query";
            gameId = game.Id;

            var nicknameChangedAt = DateTimeOffset.UtcNow.AddDays(-8);
            arrangeDb.UserProfiles.Add(new UserProfile
            {
                UserId = owner.Id,
                Nickname = "DetailOwner",
                NormalizedNickname = "DETAILOWNER",
                NicknameChangedAt = nicknameChangedAt,
                CreatedAt = nicknameChangedAt,
                UpdatedAt = nicknameChangedAt,
            });
            arrangeDb.GameTagAssignments.AddRange(
                new GameTagAssignment { GameId = game.Id, TagId = 28 },
                new GameTagAssignment { GameId = game.Id, TagId = 10 });
            arrangeDb.GamePlatformAssignments.AddRange(
                new GamePlatformAssignment { GameId = game.Id, PlatformId = 12 },
                new GamePlatformAssignment { GameId = game.Id, PlatformId = 10 });
            arrangeDb.GamePlayLinks.AddRange(
                new GamePlayLink
                {
                    GameId = game.Id,
                    DisplayName = "Second link",
                    Provider = GamePlayLinkProvider.Steam,
                    Url = "https://store.steampowered.com/app/123",
                    SortOrder = 1,
                },
                new GamePlayLink
                {
                    GameId = game.Id,
                    DisplayName = "First link",
                    Provider = GamePlayLinkProvider.DeveloperWebsite,
                    TargetPlatform = GamePlayLinkPlatform.Web,
                    Url = "https://example.test/play",
                    SortOrder = 0,
                });
            arrangeDb.FavoriteGames.Add(new FavoriteGame
            {
                UserId = viewer.Id,
                GameId = game.Id,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                owner.Id,
                game.Id);
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                owner.Id,
                game.Id);
        }

        var commandCounter = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(commandCounter);
        var storage = new EmptyObjectStorage();
        var result = await CreateService(db).GetAsync(
            gameId,
            viewerUserId,
            TestContext.Current.CancellationToken);
        var media = await new GameMediaQueryService(db, storage)
            .GetPublicDetailForExistingGameAsync(
                gameId,
                TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(4, commandCounter.Count);
        Assert.Equal(gameId, result.Game.Id);
        Assert.Equal("Measured detail query", result.Game.Description);
        Assert.Equal("DetailOwner", result.OwnerDisplayName);
        Assert.Equal("role-playing", result.Classifications.Genre.Code);
        Assert.Equal(
            ["single-player", "pixel-art"],
            result.Classifications.Tags.Select(item => item.Code));
        Assert.Equal(
            ["windows", "linux"],
            result.Classifications.Platforms.Select(item => item.Code));
        Assert.Equal(
            ["First link", "Second link"],
            result.PlayLinks.Select(item => item.DisplayName));
        Assert.Equal(GamePlayLinkPlatform.Web, result.PlayLinks[0].TargetPlatform);
        Assert.Equal(2, result.PendingRequestCount);
        Assert.True(result.IsFavorite);
        Assert.Null(media.Cover);
        Assert.Empty(media.Screenshots);
        Assert.Empty(result.Drops);
    }

    [Fact]
    public async Task GetAsync_WhenGameDoesNotExist_StopsAfterCoreQuery()
    {
        var commandCounter = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(commandCounter);

        var result = await CreateService(db).GetAsync(
            long.MaxValue,
            viewerUserId: null,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, commandCounter.Count);
    }

    private static PublicGameDetailQueryService CreateService(
        AppDbContext db)
    {
        return new PublicGameDetailQueryService(
            db,
            new PublicKeyDropReader(db));
    }

    private sealed class EmptyObjectStorage : IMediaObjectStorage
    {
        public Task<Uri> CreateDownloadUrlAsync(
            ObjectStorageDownloadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri(
                $"https://media.example.test/{request.ObjectKey.Value}"));

        public Task<Uri> CreateUploadUrlAsync(
            ObjectStorageUploadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageMetadata?> GetMetadataAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageReadResult> OpenReadAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteAsync(
            ObjectStorageWriteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyAsync(
            ObjectStorageCopyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
