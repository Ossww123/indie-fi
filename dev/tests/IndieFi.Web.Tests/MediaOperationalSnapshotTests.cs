using System.Diagnostics.Metrics;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaOperationalSnapshotTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 3, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public MediaOperationalSnapshotTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetAsync_ReportsRecoveryAndCleanupBacklogsWithoutObjectKeys()
    {
        await ResetAsync();
        await ArrangeMediaAsync();

        await using var db = _fixture.NewDbContext();
        var snapshot = await new MediaOperationalSnapshotService(db).GetAsync(
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.ProcessingPendingCount);
        Assert.Equal(2, snapshot.RecoveryDueCount);
        Assert.Equal(1, snapshot.ActiveLeaseCount);
        Assert.Equal(1, snapshot.ExpiredLeaseCount);
        Assert.Equal(3, snapshot.CleanupBacklogCount);
        Assert.Equal(1, snapshot.FailedCount);
        Assert.Equal(120, snapshot.OldestRecoveryDueAgeSeconds);
    }

    [Fact]
    public void Telemetry_ExposesBoundedAttemptAndOperationalMeasurements()
    {
        var measurements = new List<(string Name, double Value, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MediaTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Outcome(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Outcome(tags))));
        listener.Start();

        MediaTelemetry.RecordProcessingAttempt(
            MediaProcessingAttemptOutcome.TransientFailure,
            TimeSpan.FromMilliseconds(25));
        MediaTelemetry.RecordRecoveryBatch(2);
        MediaTelemetry.RecordCleanupBatch(new MediaCleanupBatchResult(
            ExaminedCount: 3,
            ExpiredCount: 1,
            ReadySourceDeletedCount: 1,
            DeletionCompletedCount: 0,
            TombstoneRemovedCount: 0,
            FailedCount: 1));
        MediaTelemetry.UpdateOperationalSnapshot(new(
            ProcessingPendingCount: 4,
            RecoveryDueCount: 2,
            ActiveLeaseCount: 1,
            ExpiredLeaseCount: 1,
            CleanupBacklogCount: 3,
            FailedCount: 1,
            OldestRecoveryDueAgeSeconds: 60));
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, item =>
            item.Name == "indiefi.media.processing.attempts"
            && item.Value == 1
            && item.Outcome == "transient_failure");
        Assert.Contains(measurements, item =>
            item.Name == "indiefi.media.recovery.items" && item.Value == 2);
        Assert.Contains(measurements, item =>
            item.Name == "indiefi.media.cleanup.items"
            && item.Value == 1
            && item.Outcome == "storage_failure");
        Assert.Contains(measurements, item =>
            item.Name == "indiefi.media.recovery.due" && item.Value == 2);
    }

    [Fact]
    public void WorkerOptions_BoundObservationInterval()
    {
        Assert.True(MediaWorkerOptions.IsValid(new()
        {
            ObservationInterval = TimeSpan.FromSeconds(5),
        }));
        Assert.False(MediaWorkerOptions.IsValid(new()
        {
            ObservationInterval = TimeSpan.FromSeconds(4),
        }));
    }

    [Fact]
    public void WorkerOptions_RequireObjectStorageWhenEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Media:Worker:Enabled"] = "true",
                ["Media:ObjectStorage:Enabled"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediaModule(configuration, MediaHostRole.Worker);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<MediaWorkerOptions>>().Value);

        Assert.Contains("object storage", exception.Message);
    }

    private async Task ArrangeMediaAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);

        var due = Media(MediaStatus.ProcessingPending, owner.Id, game.Id);
        due.NextProcessingAttemptAt = Now.AddMinutes(-2);
        var active = Media(MediaStatus.Processing, owner.Id, game.Id);
        active.ProcessingLeaseId = Guid.NewGuid();
        active.ProcessingLeaseExpiresAt = Now.AddMinutes(5);
        var expiredLease = Media(MediaStatus.Processing, owner.Id, game.Id);
        expiredLease.ProcessingLeaseId = Guid.NewGuid();
        expiredLease.ProcessingLeaseExpiresAt = Now.AddMinutes(-1);
        var deletePending = Media(MediaStatus.DeletePending, owner.Id, game.Id);
        var ready = Media(MediaStatus.Ready, owner.Id, game.Id);
        ready.IsCurrent = true;
        ready.SortOrder = 0;
        ready.PublishedAt = Now.AddMinutes(-5);
        ready.DetectedContentType = "image/png";
        ready.PixelWidth = 16;
        ready.PixelHeight = 9;
        ready.FrameCount = 1;
        var failed = Media(MediaStatus.Failed, owner.Id, game.Id);
        failed.FailureCode = "storage_unavailable";

        db.MediaAssets.AddRange(
            due,
            active,
            expiredLease,
            deletePending,
            ready,
            failed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MediaAsset Media(
        MediaStatus status,
        long ownerUserId,
        long gameId) => new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            GameId = gameId,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 128,
            SourceContentType = "image/png",
            SourceBytes = 128,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            ProcessingAttemptCount = status == MediaStatus.Processing ? 1 : 0,
            CreatedAt = Now.AddMinutes(-10),
            UpdatedAt = Now.AddMinutes(-5),
            StatusChangedAt = Now.AddMinutes(-5),
            UploadExpiresAt = Now.AddHours(23).AddMinutes(50),
        };

    private async Task ResetAsync()
    {
        await using var db = _fixture.NewOwnerDbContext();
        await db.MediaUploadAttempts.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.MediaAssets.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private static string? Outcome(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "outcome")
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }
}
