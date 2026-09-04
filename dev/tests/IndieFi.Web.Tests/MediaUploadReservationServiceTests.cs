using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaUploadReservationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaUploadReservationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReserveAsync_WithOwnedGameAndProfile_PersistsBothReservations()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1, createProfile: true);

        MediaUploadReservationResult gameResult;
        await using (var gameDb = _fixture.NewDbContext())
        {
            gameResult = await new MediaUploadReservationService(gameDb).ReserveAsync(
                target.OwnerId,
                MediaTarget.Game(target.GameIds[0]),
                MediaAttachmentKind.GameScreenshot,
                "image/png",
                1024,
                TestContext.Current.CancellationToken);
        }

        MediaUploadReservationResult profileResult;
        await using (var profileDb = _fixture.NewDbContext())
        {
            profileResult = await new MediaUploadReservationService(profileDb).ReserveAsync(
                target.OwnerId,
                MediaTarget.Profile(target.OwnerId),
                MediaAttachmentKind.ProfileImage,
                "image/webp",
                512,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(MediaUploadReservationOutcome.Reserved, gameResult.Outcome);
        Assert.Equal(MediaUploadReservationOutcome.Reserved, profileResult.Outcome);
        Assert.NotNull(gameResult.MediaId);
        Assert.NotNull(profileResult.MediaId);
        Assert.NotEqual(gameResult.MediaId, profileResult.MediaId);
        Assert.NotNull(gameResult.UploadExpiresAt);

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .Where(item => item.Id == gameResult.MediaId || item.Id == profileResult.MediaId)
            .OrderBy(item => item.AttachmentKind)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, item => Assert.Equal(MediaStatus.UploadPending, item.Status));
        Assert.Contains(stored, item => item.GameId == target.GameIds[0]);
        Assert.Contains(stored, item => item.ProfileUserId == target.OwnerId);
    }

    [Fact]
    public async Task ReserveAsync_ProfileImageForNewAccount_CreatesEmptyProfileAtomically()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 0);

        await using (var db = _fixture.NewDbContext())
        {
            var result = await new MediaUploadReservationService(db).ReserveAsync(
                target.OwnerId,
                MediaTarget.Profile(target.OwnerId),
                MediaAttachmentKind.ProfileImage,
                "image/png",
                512,
                TestContext.Current.CancellationToken);

            Assert.Equal(MediaUploadReservationOutcome.Reserved, result.Outcome);
            Assert.NotNull(result.MediaId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var profile = await verificationDb.UserProfiles
            .AsNoTracking()
            .SingleAsync(
                item => item.UserId == target.OwnerId,
                TestContext.Current.CancellationToken);
        Assert.Null(profile.Nickname);
        Assert.Empty(profile.Biography);
        Assert.True(await verificationDb.MediaAssets.AnyAsync(
            item => item.ProfileUserId == target.OwnerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReserveAsync_ProfileImageForAnotherAccount_ReturnsTargetNotFound()
    {
        var actor = await CreateOwnerTargetsAsync(gameCount: 0);
        var other = await CreateOwnerTargetsAsync(gameCount: 0);

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            actor.OwnerId,
            MediaTarget.Profile(other.OwnerId),
            MediaAttachmentKind.ProfileImage,
            "image/png",
            512,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.TargetNotFound, result.Outcome);
        Assert.False(await db.UserProfiles.AnyAsync(
            profile => profile.UserId == other.OwnerId,
            TestContext.Current.CancellationToken));
        Assert.False(await db.MediaAssets.AnyAsync(
            media => media.OwnerUserId == actor.OwnerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReserveAsync_InsideCallerTransaction_DoesNotCommitIt()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);
        Guid mediaId;
        await using (var db = _fixture.NewDbContext())
        await using (var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken))
        {
            var result = await new MediaUploadReservationService(db).ReserveAsync(
                target.OwnerId,
                MediaTarget.Game(target.GameIds[0]),
                MediaAttachmentKind.GameScreenshot,
                "image/png",
                1024,
                TestContext.Current.CancellationToken);

            Assert.Equal(MediaUploadReservationOutcome.Reserved, result.Outcome);
            mediaId = result.MediaId!.Value;
            Assert.NotNull(db.Database.CurrentTransaction);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.MediaAssets.AnyAsync(
            item => item.Id == mediaId,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("image/gif", 1)]
    [InlineData("IMAGE/PNG", 1)]
    [InlineData("image/png", 0)]
    [InlineData("image/png", MediaPolicy.GameImageMaximumBytes + 1)]
    public async Task ReserveAsync_WithInvalidDeclaredUpload_WritesNothing(
        string contentType,
        long declaredBytes)
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            target.OwnerId,
            MediaTarget.Game(target.GameIds[0]),
            MediaAttachmentKind.GameScreenshot,
            contentType,
            declaredBytes,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.InvalidUpload, result.Outcome);
        Assert.False(await db.MediaAssets.AnyAsync(
            item => item.OwnerUserId == target.OwnerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReserveAsync_WithSuspendedAccount_ReturnsUnavailable()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);
        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            var user = await ownerDb.Users.SingleAsync(
                item => item.Id == target.OwnerId,
                TestContext.Current.CancellationToken);
            user.AccountStatus = AccountStatus.IndefinitelySuspended;
            user.SuspendedUntil = null;
            await ownerDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            target.OwnerId,
            MediaTarget.Game(target.GameIds[0]),
            MediaAttachmentKind.GameScreenshot,
            "image/png",
            1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.AccountUnavailable, result.Outcome);
        Assert.Null(result.MediaId);
    }

    [Fact]
    public async Task ReserveAsync_WithForeignGame_ReturnsSameResultAsMissingTarget()
    {
        var actor = await CreateOwnerTargetsAsync(gameCount: 0);
        var other = await CreateOwnerTargetsAsync(gameCount: 1);

        await using var db = _fixture.NewDbContext();
        var service = new MediaUploadReservationService(db);
        var foreign = await service.ReserveAsync(
            actor.OwnerId,
            MediaTarget.Game(other.GameIds[0]),
            MediaAttachmentKind.GameCover,
            "image/png",
            1024,
            TestContext.Current.CancellationToken);
        var missing = await service.ReserveAsync(
            actor.OwnerId,
            MediaTarget.Game(long.MaxValue),
            MediaAttachmentKind.GameCover,
            "image/png",
            1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.TargetNotFound, foreign.Outcome);
        Assert.Equal(foreign.Outcome, missing.Outcome);
    }

    [Fact]
    public async Task ReserveAsync_WhenAccountHasTenPendingCandidates_RejectsAnother()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 11);
        await AddPendingCoversAsync(target.OwnerId, target.GameIds.Take(10));

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            target.OwnerId,
            MediaTarget.Game(target.GameIds[10]),
            MediaAttachmentKind.GameCover,
            "image/png",
            1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.PendingLimitReached, result.Outcome);
    }

    [Fact]
    public async Task ReserveAsync_WhenGameHasEightActiveScreenshots_RejectsAnother()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);
        await AddPendingScreenshotsAsync(
            target.OwnerId,
            target.GameIds[0],
            Enumerable.Repeat(1024L, MediaPolicy.GameScreenshotMaximumCount));

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            target.OwnerId,
            MediaTarget.Game(target.GameIds[0]),
            MediaAttachmentKind.GameScreenshot,
            "image/png",
            1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.AttachmentLimitReached, result.Outcome);
    }

    [Fact]
    public async Task ReserveAsync_WhenSingletonCandidateExists_RejectsSecondReplacement()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);
        await AddPendingCoversAsync(target.OwnerId, target.GameIds);

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            target.OwnerId,
            MediaTarget.Game(target.GameIds[0]),
            MediaAttachmentKind.GameCover,
            "image/png",
            1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.ReplacementInProgress, result.Outcome);
    }

    [Fact]
    public async Task ReserveAsync_WhenDeclaredTotalWouldExceedGameCapacity_RejectsIt()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);
        await AddPendingScreenshotsAsync(
            target.OwnerId,
            target.GameIds[0],
            [
                10L * 1024 * 1024,
                10L * 1024 * 1024,
                10L * 1024 * 1024,
                10L * 1024 * 1024,
                9L * 1024 * 1024,
            ]);

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            target.OwnerId,
            MediaTarget.Game(target.GameIds[0]),
            MediaAttachmentKind.GameScreenshot,
            "image/png",
            2L * 1024 * 1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.CapacityExceeded, result.Outcome);
    }

    [Fact]
    public async Task ReserveAsync_ReplacingCover_ReusesTheCurrentCoverCapacity()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);
        var gameId = target.GameIds[0];
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.MediaAssets.Add(NewReadyCover(
                target.OwnerId,
                gameId,
                MediaPolicy.GameImageMaximumBytes));
            arrangeDb.MediaAssets.AddRange(
                Enumerable.Range(0, 4).Select(_ => NewPendingScreenshot(
                    target.OwnerId,
                    gameId,
                    MediaPolicy.GameImageMaximumBytes)));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var result = await new MediaUploadReservationService(db).ReserveAsync(
            target.OwnerId,
            MediaTarget.Game(gameId),
            MediaAttachmentKind.GameCover,
            "image/png",
            MediaPolicy.GameImageMaximumBytes,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.Reserved, result.Outcome);
    }

    [Fact]
    public async Task ConcurrentReservations_CannotCrossAccountPendingLimit()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 11);
        await AddPendingCoversAsync(target.OwnerId, target.GameIds.Take(9));

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        var results = await Task.WhenAll(
            new MediaUploadReservationService(firstDb).ReserveAsync(
                target.OwnerId,
                MediaTarget.Game(target.GameIds[9]),
                MediaAttachmentKind.GameCover,
                "image/png",
                1024,
                TestContext.Current.CancellationToken),
            new MediaUploadReservationService(secondDb).ReserveAsync(
                target.OwnerId,
                MediaTarget.Game(target.GameIds[10]),
                MediaAttachmentKind.GameCover,
                "image/png",
                1024,
                TestContext.Current.CancellationToken));

        Assert.Single(results, item => item.Outcome == MediaUploadReservationOutcome.Reserved);
        Assert.Single(
            results,
            item => item.Outcome == MediaUploadReservationOutcome.PendingLimitReached);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            MediaPolicy.MaximumUploadPendingPerAccount,
            await verificationDb.MediaAssets.CountAsync(
                item => item.OwnerUserId == target.OwnerId
                    && item.Status == MediaStatus.UploadPending,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentReservations_CannotCreateNinthScreenshot()
    {
        var target = await CreateOwnerTargetsAsync(gameCount: 1);
        var gameId = target.GameIds[0];
        await AddPendingScreenshotsAsync(
            target.OwnerId,
            gameId,
            Enumerable.Repeat(1024L, MediaPolicy.GameScreenshotMaximumCount - 1));

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        var results = await Task.WhenAll(
            new MediaUploadReservationService(firstDb).ReserveAsync(
                target.OwnerId,
                MediaTarget.Game(gameId),
                MediaAttachmentKind.GameScreenshot,
                "image/png",
                1024,
                TestContext.Current.CancellationToken),
            new MediaUploadReservationService(secondDb).ReserveAsync(
                target.OwnerId,
                MediaTarget.Game(gameId),
                MediaAttachmentKind.GameScreenshot,
                "image/png",
                1024,
                TestContext.Current.CancellationToken));

        Assert.Single(results, item => item.Outcome == MediaUploadReservationOutcome.Reserved);
        Assert.Single(
            results,
            item => item.Outcome == MediaUploadReservationOutcome.AttachmentLimitReached);
    }

    private async Task<OwnerTargets> CreateOwnerTargetsAsync(
        int gameCount,
        bool createProfile = false)
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var games = Enumerable.Range(0, gameCount)
            .Select(index => new Game
            {
                OwnerId = owner.Id,
                Title = $"Media reservation game {index} {Guid.NewGuid():N}",
            })
            .ToArray();
        db.Games.AddRange(games);
        if (createProfile)
        {
            db.UserProfiles.Add(new UserProfile { UserId = owner.Id });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new OwnerTargets(owner.Id, games.Select(item => item.Id).ToArray());
    }

    private async Task AddPendingCoversAsync(long ownerId, IEnumerable<long> gameIds)
    {
        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(gameIds.Select(gameId => new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            GameId = gameId,
            AttachmentKind = MediaAttachmentKind.GameCover,
            DeclaredContentType = "image/png",
            DeclaredBytes = 1024,
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task AddPendingScreenshotsAsync(
        long ownerId,
        long gameId,
        IEnumerable<long> declaredSizes)
    {
        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(declaredSizes.Select(size =>
            NewPendingScreenshot(ownerId, gameId, size)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MediaAsset NewPendingScreenshot(
        long ownerId,
        long gameId,
        long declaredBytes) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        GameId = gameId,
        AttachmentKind = MediaAttachmentKind.GameScreenshot,
        DeclaredContentType = "image/png",
        DeclaredBytes = declaredBytes,
    };

    private static MediaAsset NewReadyCover(
        long ownerId,
        long gameId,
        long declaredBytes)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        return new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            GameId = gameId,
            AttachmentKind = MediaAttachmentKind.GameCover,
            Status = MediaStatus.Ready,
            DeclaredContentType = "image/png",
            DeclaredBytes = declaredBytes,
            SourceContentType = "image/png",
            SourceBytes = declaredBytes,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            DetectedContentType = "image/png",
            PixelWidth = 640,
            PixelHeight = 360,
            FrameCount = 1,
            ProcessingAttemptCount = 1,
            IsCurrent = true,
            PublishedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            StatusChangedAt = now,
            UploadExpiresAt = now.AddHours(24),
        };
    }

    private sealed record OwnerTargets(long OwnerId, IReadOnlyList<long> GameIds);
}
