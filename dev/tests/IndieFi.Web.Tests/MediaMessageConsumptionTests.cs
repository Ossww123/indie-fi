using IndieFi.Web.Data;
using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Contracts;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using IndieFi.Web.Modules.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaMessageConsumptionTests
{
    private readonly PostgreSqlFixture _fixture;

    public MediaMessageConsumptionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProcessingMessage_OnlyStartsForCurrentPendingRevision()
    {
        var mediaId = await CreateMediaAsync(MediaStatus.ProcessingPending);
        var eventId = MediaProcessingEventId.Create(mediaId, revision: 1);
        var message = new MediaProcessingRequestedV1(
            eventId,
            mediaId,
            Revision: 1,
            DateTimeOffset.UtcNow);
        var executor = new RecordingProcessingExecutor();

        await using var db = _fixture.NewDbContext();
        var dispatcher = new MediaProcessingMessageDispatcher(
            new MediaMessageAuthorityService(db),
            executor);

        var accepted = await dispatcher.DispatchAsync(
            eventId,
            message,
            TestContext.Current.CancellationToken);
        var wrongTransportId = await dispatcher.DispatchAsync(
            Guid.NewGuid(),
            message,
            TestContext.Current.CancellationToken);
        var wrongEventId = await dispatcher.DispatchAsync(
            eventId,
            message with { EventId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);
        var staleRevision = await dispatcher.DispatchAsync(
            MediaProcessingEventId.Create(mediaId, revision: 2),
            message with
            {
                EventId = MediaProcessingEventId.Create(mediaId, revision: 2),
                Revision = 2,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaMessageAuthorityOutcome.Accepted, accepted);
        Assert.Equal(
            MediaMessageAuthorityOutcome.InvalidContract,
            wrongTransportId);
        Assert.Equal(MediaMessageAuthorityOutcome.InvalidContract, wrongEventId);
        Assert.Equal(
            MediaMessageAuthorityOutcome.RevisionMismatch,
            staleRevision);
        Assert.Equal([(mediaId, 1)], executor.Started);
    }

    [Fact]
    public async Task Messages_ForMissingOrIneligibleMedia_AreIgnored()
    {
        var mediaId = await CreateMediaAsync(MediaStatus.DeletePending);
        var processingEventId = MediaProcessingEventId.Create(mediaId, revision: 1);
        var processingExecutor = new RecordingProcessingExecutor();
        var missingMediaId = Guid.NewGuid();
        var missingEventId = MediaProcessingEventId.Create(
            missingMediaId,
            revision: 1);

        await using var db = _fixture.NewDbContext();
        var dispatcher = new MediaProcessingMessageDispatcher(
            new MediaMessageAuthorityService(db),
            processingExecutor);

        var wrongState = await dispatcher.DispatchAsync(
            processingEventId,
            new MediaProcessingRequestedV1(
                processingEventId,
                mediaId,
                Revision: 1,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        var missing = await dispatcher.DispatchAsync(
            missingEventId,
            new MediaProcessingRequestedV1(
                missingEventId,
                missingMediaId,
                Revision: 1,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaMessageAuthorityOutcome.StatusMismatch, wrongState);
        Assert.Equal(MediaMessageAuthorityOutcome.MediaNotFound, missing);
        Assert.Empty(processingExecutor.Started);
    }

    [Fact]
    public async Task DeletionMessage_OnlyStartsForCurrentDeletePendingRevision()
    {
        var mediaId = await CreateMediaAsync(MediaStatus.DeletePending);
        var eventId = MediaDeletionEventId.Create(mediaId, revision: 1);
        var message = new MediaDeletionRequestedV1(
            eventId,
            mediaId,
            Revision: 1,
            DateTimeOffset.UtcNow);
        var executor = new RecordingDeletionExecutor();

        await using var db = _fixture.NewDbContext();
        var dispatcher = new MediaDeletionMessageDispatcher(
            new MediaMessageAuthorityService(db),
            executor);

        var accepted = await dispatcher.DispatchAsync(
            eventId,
            message,
            TestContext.Current.CancellationToken);
        var missingMessageId = await dispatcher.DispatchAsync(
            null,
            message,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaMessageAuthorityOutcome.Accepted, accepted);
        Assert.Equal(
            MediaMessageAuthorityOutcome.InvalidContract,
            missingMessageId);
        Assert.Equal([(mediaId, 1)], executor.Started);
    }

    [Fact]
    public void WorkerConfiguration_ControlsMediaConsumerRegistration()
    {
        var enabledConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MediaWorkerOptions.SectionName}:Enabled"] = "true",
            })
            .Build();
        var disabledConfiguration = new ConfigurationBuilder().Build();
        var enabledServices = NewMessagingServices(enabledConfiguration);
        var disabledServices = NewMessagingServices(disabledConfiguration);

        Assert.Contains(
            enabledServices,
            descriptor => descriptor.ServiceType
                == typeof(MediaProcessingRequestedConsumer));
        Assert.Contains(
            enabledServices,
            descriptor => descriptor.ServiceType
                == typeof(MediaDeletionRequestedConsumer));
        Assert.DoesNotContain(
            disabledServices,
            descriptor => descriptor.ServiceType
                == typeof(MediaProcessingRequestedConsumer));
        Assert.DoesNotContain(
            disabledServices,
            descriptor => descriptor.ServiceType
                == typeof(MediaDeletionRequestedConsumer));
    }

    private async Task<Guid> CreateMediaAsync(MediaStatus status)
    {
        await using var db = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var media = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            GameId = game.Id,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 2048,
        };
        if (status == MediaStatus.ProcessingPending)
        {
            media.SourceContentType = media.DeclaredContentType;
            media.SourceBytes = media.DeclaredBytes;
            media.SourceETag = $"\"{Guid.NewGuid():N}\"";
        }

        db.MediaAssets.Add(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return media.Id;
    }

    private static ServiceCollection NewMessagingServices(
        IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=unused"));
        services.AddMediaModule(configuration, MediaHostRole.Worker);
        services.AddNotificationsModule(
            configuration,
            registration => registration.AddMediaConsumers(configuration));
        return services;
    }

    private sealed class RecordingProcessingExecutor :
        IMediaProcessingWorkExecutor
    {
        public List<(Guid MediaId, int Revision)> Started { get; } = [];

        public Task ExecuteAsync(
            Guid mediaId,
            int revision,
            CancellationToken cancellationToken = default)
        {
            Started.Add((mediaId, revision));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDeletionExecutor : IMediaDeletionWorkExecutor
    {
        public List<(Guid MediaId, int Revision)> Started { get; } = [];

        public Task ExecuteAsync(
            Guid mediaId,
            int revision,
            CancellationToken cancellationToken = default)
        {
            Started.Add((mediaId, revision));
            return Task.CompletedTask;
        }
    }
}
