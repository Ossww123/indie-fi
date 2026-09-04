using System.Collections.Concurrent;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Contracts;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using IndieFi.Web.Modules.Notifications;
using IndieFi.Web.Modules.Profiles.Domain;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaPublicationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaPublicationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublishReadyAsync_ReplacesCoverAndStagesDeletionOnce()
    {
        var scenario = await CreateGameScenarioAsync();
        var oldCover = NewReadyMedia(
            scenario,
            MediaAttachmentKind.GameCover);
        var candidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameCover);
        await AddMediaAsync(oldCover, candidate);
        var publisher = new RecordingDeletionPublisher();

        MediaPublishReadyResult first;
        await using (var db = _fixture.NewDbContext())
        {
            first = await NewService(db, publisher, scenario.ObservedAt)
                .PublishReadyAsync(
                    candidate.Id,
                    candidate.ProcessingLeaseId!.Value,
                    TestContext.Current.CancellationToken);
        }

        MediaPublishReadyResult repeated;
        await using (var db = _fixture.NewDbContext())
        {
            repeated = await NewService(
                db,
                publisher,
                scenario.ObservedAt.AddSeconds(1)).PublishReadyAsync(
                    candidate.Id,
                    candidate.ProcessingLeaseId!.Value,
                    TestContext.Current.CancellationToken);
        }

        var deletionEventId = MediaDeletionEventId.Create(
            oldCover.Id,
            oldCover.Revision);
        Assert.Equal(MediaPublishReadyOutcome.Published, first.Outcome);
        Assert.Equal(deletionEventId, first.ReplacedMediaDeletionEventId);
        Assert.Equal(MediaPublishReadyOutcome.AlreadyReady, repeated.Outcome);
        var message = Assert.Single(publisher.Messages);
        Assert.Equal(deletionEventId, message.EventId);
        Assert.Equal(oldCover.Id, message.MediaId);

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .Where(item => item.Id == oldCover.Id || item.Id == candidate.Id)
            .ToDictionaryAsync(
                item => item.Id,
                TestContext.Current.CancellationToken);
        AssertDisconnected(stored[oldCover.Id]);
        Assert.Equal(MediaStatus.Ready, stored[candidate.Id].Status);
        Assert.True(stored[candidate.Id].IsCurrent);
        Assert.Equal(scenario.ObservedAt, stored[candidate.Id].PublishedAt);
        Assert.Null(stored[candidate.Id].ProcessingLeaseId);
        Assert.Null(stored[candidate.Id].ProcessingLeaseExpiresAt);
    }

    [Fact]
    public async Task PublishReadyAsync_ReplacesProfileImageWithinProfileTarget()
    {
        var scenario = await CreateProfileScenarioAsync();
        var oldImage = NewReadyProfileMedia(scenario);
        var candidate = NewProcessingProfileMedia(scenario);
        await AddMediaAsync(oldImage, candidate);
        var publisher = new RecordingDeletionPublisher();

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, publisher, scenario.ObservedAt)
            .PublishReadyAsync(
                candidate.Id,
                candidate.ProcessingLeaseId!.Value,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaPublishReadyOutcome.Published, result.Outcome);
        Assert.Equal(
            MediaDeletionEventId.Create(oldImage.Id, oldImage.Revision),
            result.ReplacedMediaDeletionEventId);
        Assert.Single(publisher.Messages);

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .Where(item => item.Id == oldImage.Id || item.Id == candidate.Id)
            .ToDictionaryAsync(
                item => item.Id,
                TestContext.Current.CancellationToken);
        AssertDisconnected(stored[oldImage.Id]);
        Assert.Equal(MediaStatus.Ready, stored[candidate.Id].Status);
        Assert.True(stored[candidate.Id].IsCurrent);
        Assert.Equal(scenario.OwnerId, stored[candidate.Id].ProfileUserId);
        Assert.Null(stored[candidate.Id].GameId);
    }

    [Fact]
    public async Task PublishReadyAsync_WithLostLeaseOrIncompleteValidation_KeepsCurrentCover()
    {
        var scenario = await CreateGameScenarioAsync();
        var oldCover = NewReadyMedia(
            scenario,
            MediaAttachmentKind.GameCover);
        var candidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameCover,
            validationComplete: false);
        await AddMediaAsync(oldCover, candidate);
        var publisher = new RecordingDeletionPublisher();

        await using var db = _fixture.NewDbContext();
        var service = NewService(db, publisher, scenario.ObservedAt);
        var lostLease = await service.PublishReadyAsync(
            candidate.Id,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);
        var incomplete = await service.PublishReadyAsync(
            candidate.Id,
            candidate.ProcessingLeaseId!.Value,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaPublishReadyOutcome.ProcessingLeaseLost, lostLease.Outcome);
        Assert.Equal(MediaPublishReadyOutcome.ValidationIncomplete, incomplete.Outcome);
        Assert.Empty(publisher.Messages);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            MediaStatus.Ready,
            await verificationDb.MediaAssets
                .Where(item => item.Id == oldCover.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            MediaStatus.Processing,
            await verificationDb.MediaAssets
                .Where(item => item.Id == candidate.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishReadyAsync_WithExpiredLease_KeepsCandidatePrivate()
    {
        var scenario = await CreateGameScenarioAsync();
        var candidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot);
        await AddMediaAsync(candidate);

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            new RecordingDeletionPublisher(),
            scenario.ObservedAt.AddMinutes(5)).PublishReadyAsync(
                candidate.Id,
                candidate.ProcessingLeaseId!.Value,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaPublishReadyOutcome.ProcessingLeaseLost, result.Outcome);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == candidate.Id,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Processing, stored.Status);
        Assert.False(stored.IsCurrent);
        Assert.Null(stored.PublishedAt);
    }

    [Fact]
    public async Task ConcurrentScreenshotPublication_AssignsDistinctReadyOrders()
    {
        var scenario = await CreateGameScenarioAsync();
        var existing = NewReadyMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot,
            sortOrder: 0);
        var firstCandidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot);
        var secondCandidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot);
        await AddMediaAsync(existing, firstCandidate, secondCandidate);
        var publisher = new RecordingDeletionPublisher();

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        var results = await Task.WhenAll(
            NewService(firstDb, publisher, scenario.ObservedAt)
                .PublishReadyAsync(
                    firstCandidate.Id,
                    firstCandidate.ProcessingLeaseId!.Value,
                    TestContext.Current.CancellationToken),
            NewService(secondDb, publisher, scenario.ObservedAt)
                .PublishReadyAsync(
                    secondCandidate.Id,
                    secondCandidate.ProcessingLeaseId!.Value,
                    TestContext.Current.CancellationToken));

        Assert.All(
            results,
            result => Assert.Equal(MediaPublishReadyOutcome.Published, result.Outcome));
        Assert.Equal(
            new int?[] { 1, 2 },
            results.Select(result => result.ScreenshotSortOrder).Order().ToArray());
        Assert.Empty(publisher.Messages);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            new[] { 0, 1, 2 },
            await verificationDb.MediaAssets
                .Where(item => item.GameId == scenario.GameId
                    && item.AttachmentKind == MediaAttachmentKind.GameScreenshot
                    && item.Status == MediaStatus.Ready)
                .OrderBy(item => item.SortOrder)
                .Select(item => item.SortOrder!.Value)
                .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishReadyAsync_WithEightReadyScreenshots_KeepsCandidatePrivate()
    {
        var scenario = await CreateGameScenarioAsync();
        var screenshots = Enumerable
            .Range(0, MediaPolicy.GameScreenshotMaximumCount)
            .Select(order => NewReadyMedia(
                scenario,
                MediaAttachmentKind.GameScreenshot,
                order))
            .ToList();
        var candidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot);
        screenshots.Add(candidate);
        await AddMediaAsync(screenshots.ToArray());

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            new RecordingDeletionPublisher(),
            scenario.ObservedAt).PublishReadyAsync(
                candidate.Id,
                candidate.ProcessingLeaseId!.Value,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaPublishReadyOutcome.ScreenshotLimitReached, result.Outcome);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == candidate.Id,
                TestContext.Current.CancellationToken);
        Assert.Equal(MediaStatus.Processing, stored.Status);
        Assert.False(stored.IsCurrent);
        Assert.Null(stored.SortOrder);
        Assert.Null(stored.PublishedAt);
    }

    [Fact]
    public async Task RemoveAsync_DisconnectsReadyScreenshotAndIsIdempotent()
    {
        var scenario = await CreateGameScenarioAsync();
        var screenshot = NewReadyMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot,
            sortOrder: 3);
        await AddMediaAsync(screenshot);
        var publisher = new RecordingDeletionPublisher();

        MediaRemovalResult first;
        await using (var db = _fixture.NewDbContext())
        {
            first = await NewService(db, publisher, scenario.ObservedAt)
                .RemoveAsync(
                    scenario.OwnerId,
                    screenshot.Id,
                    TestContext.Current.CancellationToken);
        }

        MediaRemovalResult repeated;
        await using (var db = _fixture.NewDbContext())
        {
            repeated = await NewService(
                db,
                publisher,
                scenario.ObservedAt.AddSeconds(1)).RemoveAsync(
                    scenario.OwnerId,
                    screenshot.Id,
                    TestContext.Current.CancellationToken);
        }

        var eventId = MediaDeletionEventId.Create(
            screenshot.Id,
            screenshot.Revision);
        Assert.Equal(MediaRemovalOutcome.Removed, first.Outcome);
        Assert.Equal(eventId, first.DeletionEventId);
        Assert.Equal(MediaRemovalOutcome.AlreadyRemoved, repeated.Outcome);
        Assert.Equal(eventId, repeated.DeletionEventId);
        Assert.Single(publisher.Messages);

        await using var verificationDb = _fixture.NewDbContext();
        AssertDisconnected(await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == screenshot.Id,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveAsync_ProcessingCandidate_ClearsItsLease()
    {
        var scenario = await CreateGameScenarioAsync();
        var candidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot);
        await AddMediaAsync(candidate);

        await using var db = _fixture.NewDbContext();
        var result = await NewService(
            db,
            new RecordingDeletionPublisher(),
            scenario.ObservedAt).RemoveAsync(
                scenario.OwnerId,
                candidate.Id,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaRemovalOutcome.Removed, result.Outcome);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.MediaAssets
            .AsNoTracking()
            .SingleAsync(
                item => item.Id == candidate.Id,
                TestContext.Current.CancellationToken);
        AssertDisconnected(stored);
        Assert.Null(stored.ProcessingLeaseId);
        Assert.Null(stored.ProcessingLeaseExpiresAt);
    }

    [Fact]
    public async Task RemoveAsync_ForForeignOwner_HidesMedia()
    {
        var scenario = await CreateGameScenarioAsync();
        var screenshot = NewReadyMedia(
            scenario,
            MediaAttachmentKind.GameScreenshot,
            sortOrder: 0);
        await AddMediaAsync(screenshot);
        long otherOwnerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherOwnerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        var publisher = new RecordingDeletionPublisher();
        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, publisher, scenario.ObservedAt)
            .RemoveAsync(
                otherOwnerId,
                screenshot.Id,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaRemovalOutcome.MediaNotFound, result.Outcome);
        Assert.Empty(publisher.Messages);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            MediaStatus.Ready,
            await verificationDb.MediaAssets
                .Where(item => item.Id == screenshot.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CoverReplacement_CallerRollback_RestoresCurrentAndOutboxThenRetries()
    {
        var scenario = await CreateGameScenarioAsync();
        var oldCover = NewReadyMedia(
            scenario,
            MediaAttachmentKind.GameCover);
        var candidate = NewProcessingMedia(
            scenario,
            MediaAttachmentKind.GameCover);
        await AddMediaAsync(oldCover, candidate);
        await using var provider = BuildOutboxServices(scenario.ObservedAt);
        var eventId = MediaDeletionEventId.Create(oldCover.Id, oldCover.Revision);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            var result = await scope.ServiceProvider
                .GetRequiredService<MediaPublicationService>()
                .PublishReadyAsync(
                    candidate.Id,
                    candidate.ProcessingLeaseId!.Value,
                    TestContext.Current.CancellationToken);

            Assert.Equal(MediaPublishReadyOutcome.Published, result.Outcome);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (var rolledBackDb = _fixture.NewDbContext())
        {
            Assert.Equal(
                MediaStatus.Ready,
                await rolledBackDb.MediaAssets
                    .Where(item => item.Id == oldCover.Id)
                    .Select(item => item.Status)
                    .SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                MediaStatus.Processing,
                await rolledBackDb.MediaAssets
                    .Where(item => item.Id == candidate.Id)
                    .Select(item => item.Status)
                    .SingleAsync(TestContext.Current.CancellationToken));
            Assert.False(await rolledBackDb.Set<OutboxMessage>().AnyAsync(
                item => item.MessageId == eventId,
                TestContext.Current.CancellationToken));
        }

        await using (var retryScope = provider.CreateAsyncScope())
        {
            var recovered = await retryScope.ServiceProvider
                .GetRequiredService<MediaPublicationService>()
                .PublishReadyAsync(
                    candidate.Id,
                    candidate.ProcessingLeaseId!.Value,
                    TestContext.Current.CancellationToken);
            Assert.Equal(MediaPublishReadyOutcome.Published, recovered.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            MediaStatus.DeletePending,
            await verificationDb.MediaAssets
                .Where(item => item.Id == oldCover.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            MediaStatus.Ready,
            await verificationDb.MediaAssets
                .Where(item => item.Id == candidate.Id)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        var outbox = await verificationDb.Set<OutboxMessage>().SingleAsync(
            item => item.MessageId == eventId,
            TestContext.Current.CancellationToken);
        Assert.Contains(nameof(MediaDeletionRequestedV1), outbox.MessageType);
        Assert.Contains(oldCover.Id.ToString(), outbox.Body);
        Assert.DoesNotContain("sources/", outbox.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("derived/", outbox.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oldCover.SourceETag!, outbox.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ownerUserId",
            outbox.Body,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<GameScenario> CreateGameScenarioAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Media publication game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var createdAt = PostgreSqlTimestamp(DateTimeOffset.UtcNow.AddSeconds(1));
        return new GameScenario(
            owner.Id,
            game.Id,
            createdAt,
            createdAt.AddMinutes(1));
    }

    private async Task<ProfileScenario> CreateProfileScenarioAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        db.UserProfiles.Add(new UserProfile { UserId = owner.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var createdAt = PostgreSqlTimestamp(DateTimeOffset.UtcNow.AddSeconds(1));
        return new ProfileScenario(
            owner.Id,
            createdAt,
            createdAt.AddMinutes(1));
    }

    private async Task AddMediaAsync(params MediaAsset[] media)
    {
        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MediaAsset NewReadyMedia(
        GameScenario scenario,
        MediaAttachmentKind attachmentKind,
        int? sortOrder = null)
    {
        return NewMedia(
            scenario.OwnerId,
            scenario.GameId,
            profileUserId: null,
            attachmentKind,
            scenario.CreatedAt,
            scenario.ObservedAt,
            MediaStatus.Ready,
            sortOrder);
    }

    private static MediaAsset NewProcessingMedia(
        GameScenario scenario,
        MediaAttachmentKind attachmentKind,
        bool validationComplete = true)
    {
        return NewMedia(
            scenario.OwnerId,
            scenario.GameId,
            profileUserId: null,
            attachmentKind,
            scenario.CreatedAt,
            scenario.ObservedAt,
            MediaStatus.Processing,
            validationComplete: validationComplete);
    }

    private static MediaAsset NewReadyProfileMedia(
        ProfileScenario scenario)
    {
        return NewMedia(
            scenario.OwnerId,
            gameId: null,
            scenario.OwnerId,
            MediaAttachmentKind.ProfileImage,
            scenario.CreatedAt,
            scenario.ObservedAt,
            MediaStatus.Ready);
    }

    private static MediaAsset NewProcessingProfileMedia(
        ProfileScenario scenario)
    {
        return NewMedia(
            scenario.OwnerId,
            gameId: null,
            scenario.OwnerId,
            MediaAttachmentKind.ProfileImage,
            scenario.CreatedAt,
            scenario.ObservedAt,
            MediaStatus.Processing);
    }

    private static MediaAsset NewMedia(
        long ownerUserId,
        long? gameId,
        long? profileUserId,
        MediaAttachmentKind attachmentKind,
        DateTimeOffset createdAt,
        DateTimeOffset observedAt,
        MediaStatus status,
        int? sortOrder = null,
        bool validationComplete = true)
    {
        var isReady = status == MediaStatus.Ready;
        var isProcessing = status == MediaStatus.Processing;
        return new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            GameId = gameId,
            ProfileUserId = profileUserId,
            AttachmentKind = attachmentKind,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 2048,
            SourceContentType = "image/png",
            SourceBytes = 2048,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            DetectedContentType = validationComplete ? "image/png" : null,
            PixelWidth = validationComplete ? 512 : null,
            PixelHeight = validationComplete ? 360 : null,
            FrameCount = validationComplete ? 1 : null,
            ProcessingAttemptCount = 1,
            ProcessingLeaseId = isProcessing ? Guid.NewGuid() : null,
            ProcessingLeaseExpiresAt = isProcessing
                ? observedAt.AddMinutes(5)
                : null,
            IsCurrent = isReady,
            SortOrder = isReady ? sortOrder : null,
            PublishedAt = isReady ? createdAt : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            StatusChangedAt = createdAt,
            UploadExpiresAt = createdAt.AddHours(24),
        };
    }

    private static MediaPublicationService NewService(
        AppDbContext db,
        IMediaDeletionPublisher publisher,
        DateTimeOffset now) =>
        new(db, publisher, new FixedTimeProvider(now));

    private ServiceProvider BuildOutboxServices(DateTimeOffset now)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        services.AddScoped<
            IMediaDeletionPublisher,
            MassTransitMediaDeletionPublisher>();
        services.AddScoped<MediaPublicationService>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static void AssertDisconnected(MediaAsset media)
    {
        Assert.Equal(MediaStatus.DeletePending, media.Status);
        Assert.False(media.IsCurrent);
        Assert.Null(media.SortOrder);
        Assert.Null(media.PublishedAt);
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private sealed record GameScenario(
        long OwnerId,
        long GameId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ObservedAt);

    private sealed record ProfileScenario(
        long OwnerId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ObservedAt);

    private sealed record StagedDeletionMessage(
        Guid EventId,
        Guid MediaId,
        int Revision,
        DateTimeOffset OccurredAt);

    private sealed class RecordingDeletionPublisher : IMediaDeletionPublisher
    {
        private readonly ConcurrentQueue<StagedDeletionMessage> _messages = [];

        public IReadOnlyCollection<StagedDeletionMessage> Messages =>
            _messages.ToArray();

        public Task StageAsync(
            Guid eventId,
            Guid mediaId,
            int revision,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            _messages.Enqueue(new StagedDeletionMessage(
                eventId,
                mediaId,
                revision,
                occurredAt));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
