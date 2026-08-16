using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaMetadataStorageTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaMetadataStorageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ValidGameAndProfileCandidates_PersistWithOpaqueUploadAttempts()
    {
        var (ownerId, gameId, profileUserId) = await CreateTargetsAsync();
        var gameMedia = NewPendingGameMedia(ownerId, gameId, MediaAttachmentKind.GameScreenshot);
        var profileMedia = NewPendingProfileMedia(profileUserId);
        var gameAttempt = NewAttempt(gameMedia);
        var profileAttempt = NewAttempt(profileMedia);

        await using (var db = _fixture.NewDbContext())
        {
            db.AddRange(gameMedia, profileMedia, gameAttempt, profileAttempt);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .Include(item => item.UploadAttempts)
            .Where(item => item.Id == gameMedia.Id || item.Id == profileMedia.Id)
            .OrderBy(item => item.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stored.Count);
        Assert.All(stored, item =>
        {
            Assert.Equal(MediaStatus.UploadPending, item.Status);
            Assert.Equal(1, item.Revision);
            Assert.False(item.IsCurrent);
            Assert.Equal(TimeSpan.FromHours(24), item.UploadExpiresAt - item.CreatedAt);
            var attempt = Assert.Single(item.UploadAttempts);
            Assert.Equal(TimeSpan.FromMinutes(15), attempt.UrlExpiresAt - attempt.IssuedAt);
            Assert.Null(attempt.SupersededAt);
            Assert.Null(attempt.CompletedAt);
        });
    }

    [Fact]
    public async Task GameMedia_OwnerMustMatchTheGameOwner()
    {
        long gameId;
        long otherUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
            otherUserId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            gameId = (await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id)).Id;
        }

        await using var db = _fixture.NewDbContext();
        db.MediaAssets.Add(NewPendingGameMedia(
            otherUserId,
            gameId,
            MediaAttachmentKind.GameScreenshot));

        var postgres = await SaveExpectingPostgresAsync(db);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
        Assert.Equal("FK_media_assets_games_game_id_owner_user_id", postgres.ConstraintName);
    }

    [Fact]
    public async Task TargetShape_RejectsCrossTargetAttachment()
    {
        var (ownerId, gameId, _) = await CreateTargetsAsync();
        var invalid = NewPendingGameMedia(
            ownerId,
            gameId,
            MediaAttachmentKind.GameScreenshot);
        invalid.AttachmentKind = MediaAttachmentKind.ProfileImage;

        await using var db = _fixture.NewDbContext();
        db.MediaAssets.Add(invalid);

        var postgres = await SaveExpectingPostgresAsync(db);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_media_assets_target_shape", postgres.ConstraintName);
    }

    [Fact]
    public async Task DeclaredUpload_RejectsBytesOverTheAttachmentLimit()
    {
        var (ownerId, gameId, _) = await CreateTargetsAsync();
        var invalid = NewPendingGameMedia(
            ownerId,
            gameId,
            MediaAttachmentKind.GameScreenshot);
        invalid.DeclaredBytes = MediaPolicy.GameImageMaximumBytes + 1;

        await using var db = _fixture.NewDbContext();
        db.MediaAssets.Add(invalid);

        var postgres = await SaveExpectingPostgresAsync(db);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_media_assets_declared_upload", postgres.ConstraintName);
    }

    [Fact]
    public async Task PinnedSourceMetadata_MustBeStoredAsOneCompleteSet()
    {
        var (ownerId, gameId, _) = await CreateTargetsAsync();
        var invalid = NewPendingGameMedia(
            ownerId,
            gameId,
            MediaAttachmentKind.GameScreenshot);
        invalid.Status = MediaStatus.ProcessingPending;
        invalid.SourceContentType = invalid.DeclaredContentType;

        await using var db = _fixture.NewDbContext();
        db.MediaAssets.Add(invalid);

        var postgres = await SaveExpectingPostgresAsync(db);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_media_assets_source_metadata", postgres.ConstraintName);
    }

    [Fact]
    public async Task DetectedImageMetadata_MustBeStoredAsOneCompleteSet()
    {
        var (ownerId, gameId, _) = await CreateTargetsAsync();
        var invalid = NewReadyScreenshot(
            ownerId,
            gameId,
            sortOrder: 0,
            DateTimeOffset.UtcNow.AddSeconds(1));
        invalid.PixelWidth = null;

        await using var db = _fixture.NewDbContext();
        db.MediaAssets.Add(invalid);

        var postgres = await SaveExpectingPostgresAsync(db);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.Equal("CK_media_assets_detected_image", postgres.ConstraintName);
    }

    [Fact]
    public async Task ActiveSingletonCandidates_AreUniquePerTarget()
    {
        var (ownerId, gameId, profileUserId) = await CreateTargetsAsync();

        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(
            NewPendingGameMedia(ownerId, gameId, MediaAttachmentKind.GameCover),
            NewPendingGameMedia(ownerId, gameId, MediaAttachmentKind.GameCover));

        var gameViolation = await SaveExpectingPostgresAsync(db);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, gameViolation.SqlState);
        Assert.Equal(
            "IX_media_assets_active_game_cover_candidate",
            gameViolation.ConstraintName);

        await using var profileDb = _fixture.NewDbContext();
        profileDb.MediaAssets.AddRange(
            NewPendingProfileMedia(profileUserId),
            NewPendingProfileMedia(profileUserId));

        var profileViolation = await SaveExpectingPostgresAsync(profileDb);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, profileViolation.SqlState);
        Assert.Equal(
            "IX_media_assets_active_profile_image_candidate",
            profileViolation.ConstraintName);
    }

    [Fact]
    public async Task CurrentScreenshots_CannotShareOnePublicOrder()
    {
        var (ownerId, gameId, _) = await CreateTargetsAsync();
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(
            NewReadyScreenshot(ownerId, gameId, sortOrder: 0, now),
            NewReadyScreenshot(ownerId, gameId, sortOrder: 0, now));

        var postgres = await SaveExpectingPostgresAsync(db);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(
            "IX_media_assets_current_game_screenshot_order",
            postgres.ConstraintName);
    }

    [Fact]
    public async Task UploadAttempts_RequireNewIdentityAndOneCurrentAttempt()
    {
        var (ownerId, gameId, _) = await CreateTargetsAsync();
        var media = NewPendingGameMedia(
            ownerId,
            gameId,
            MediaAttachmentKind.GameScreenshot);
        var first = NewAttempt(media);

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.AddRange(media, first);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var duplicateDb = _fixture.NewDbContext())
        {
            duplicateDb.MediaUploadAttempts.Add(NewAttempt(media));
            var postgres = await SaveExpectingPostgresAsync(duplicateDb);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("IX_media_upload_attempts_current", postgres.ConstraintName);
        }

        await using (var supersedeDb = _fixture.NewDbContext())
        {
            var current = await supersedeDb.MediaUploadAttempts.SingleAsync(
                item => item.Id == first.Id,
                TestContext.Current.CancellationToken);
            current.SupersededAt = DateTimeOffset.UtcNow;
            await supersedeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var replacement = new MediaUploadAttempt
        {
            Id = Guid.NewGuid(),
            MediaId = media.Id,
        };
        await using (var reissueDb = _fixture.NewDbContext())
        {
            reissueDb.MediaUploadAttempts.Add(replacement);
            await reissueDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.NotEqual(first.Id, replacement.Id);
        Assert.NotEqual(first.UrlExpiresAt, replacement.UrlExpiresAt);
    }

    [Fact]
    public async Task ApplicationRole_CannotRewriteReservedIdentityButCanAdvanceLifecycle()
    {
        var (ownerId, gameId, _) = await CreateTargetsAsync();
        var media = NewPendingGameMedia(
            ownerId,
            gameId,
            MediaAttachmentKind.GameScreenshot);
        var attempt = NewAttempt(media);

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            arrangeDb.AddRange(media, attempt);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var immutableDb = _fixture.NewDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                immutableDb.MediaAssets
                    .Where(item => item.Id == media.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            item => item.DeclaredBytes,
                            item => item.DeclaredBytes - 1),
                        TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }

        await using (var lifecycleDb = _fixture.NewDbContext())
        {
            var changedAt = DateTimeOffset.UtcNow;
            var updated = await lifecycleDb.MediaAssets
                .Where(item => item.Id == media.Id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, MediaStatus.Expired)
                        .SetProperty(item => item.StatusChangedAt, changedAt)
                        .SetProperty(item => item.UpdatedAt, changedAt),
                    TestContext.Current.CancellationToken);
            Assert.Equal(1, updated);
        }

        await using var attemptIdentityDb = _fixture.NewDbContext();
        var replacementId = Guid.NewGuid();
        var attemptException = await Assert.ThrowsAsync<PostgresException>(() =>
            attemptIdentityDb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE media_upload_attempts SET id = {replacementId} WHERE id = {attempt.Id}",
                TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, attemptException.SqlState);
    }

    private async Task<(long OwnerId, long GameId, long ProfileUserId)> CreateTargetsAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        db.UserProfiles.Add(new UserProfile { UserId = owner.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (owner.Id, game.Id, owner.Id);
    }

    private static MediaAsset NewPendingGameMedia(
        long ownerId,
        long gameId,
        MediaAttachmentKind attachmentKind) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        GameId = gameId,
        AttachmentKind = attachmentKind,
        DeclaredContentType = "image/png",
        DeclaredBytes = 1024,
    };

    private static MediaAsset NewPendingProfileMedia(long userId) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = userId,
        ProfileUserId = userId,
        AttachmentKind = MediaAttachmentKind.ProfileImage,
        DeclaredContentType = "image/webp",
        DeclaredBytes = 512,
    };

    private static MediaUploadAttempt NewAttempt(MediaAsset media) => new()
    {
        Id = Guid.NewGuid(),
        MediaId = media.Id,
    };

    private static MediaAsset NewReadyScreenshot(
        long ownerId,
        long gameId,
        int sortOrder,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        GameId = gameId,
        AttachmentKind = MediaAttachmentKind.GameScreenshot,
        Status = MediaStatus.Ready,
        DeclaredContentType = "image/png",
        DeclaredBytes = 1024,
        SourceContentType = "image/png",
        SourceBytes = 1024,
        SourceETag = $"\"{Guid.NewGuid():N}\"",
        DetectedContentType = "image/png",
        PixelWidth = 640,
        PixelHeight = 360,
        FrameCount = 1,
        ProcessingAttemptCount = 1,
        IsCurrent = true,
        SortOrder = sortOrder,
        PublishedAt = now,
        CreatedAt = now,
        UpdatedAt = now,
        StatusChangedAt = now,
        UploadExpiresAt = now.AddHours(24),
    };

    private static async Task<PostgresException> SaveExpectingPostgresAsync(
        DbContext db)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.SaveChangesAsync(TestContext.Current.CancellationToken));
        return Assert.IsType<PostgresException>(exception.InnerException);
    }
}
