using System.Net;
using System.Net.Http.Headers;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using IndieFi.Web.Modules.Notifications;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MediaWorkerIntegrationTests :
    IClassFixture<RabbitMqFixture>,
    IClassFixture<MinioFixture>
{
    private readonly PostgreSqlFixture _postgres;
    private readonly RabbitMqFixture _rabbitMq;
    private readonly MinioFixture _minio;

    public MediaWorkerIntegrationTests(
        PostgreSqlFixture postgres,
        RabbitMqFixture rabbitMq,
        MinioFixture minio)
    {
        _postgres = postgres;
        _rabbitMq = rabbitMq;
        _minio = minio;
    }

    [Fact]
    public async Task TwoWorkers_DuplicateRestartAndStorageOutage_ConvergeToReady()
    {
        await ResetAsync();
        var settings = BuildSettings();
        using var firstWorker = BuildHost(settings, workerEnabled: true);
        using var secondWorker = BuildHost(settings, workerEnabled: true);
        using var producer = BuildHost(settings, workerEnabled: false);
        var minioAccessSuspended = false;

        await firstWorker.StartAsync(TestContext.Current.CancellationToken);
        await secondWorker.StartAsync(TestContext.Current.CancellationToken);
        await producer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForProcessingConsumerCountAsync(settings, expectedCount: 2);

            var competed = await ArrangeMediaAsync(
                producer.Services,
                MediaStatus.ProcessingPending);
            await StageAsync(producer.Services, competed.MediaId);
            await WaitForStateAsync(
                competed.MediaId,
                MediaStatus.Ready,
                attemptCount: 1);
            var firstDerivativeEtag = await GetDerivativeEtagAsync(
                producer.Services,
                competed.MediaId);

            await StageAsync(producer.Services, competed.MediaId);
            await WaitUntilOutboxIsEmptyAsync(competed.EventId);
            await Task.Delay(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);
            await AssertStateAsync(
                competed.MediaId,
                MediaStatus.Ready,
                attemptCount: 1);
            Assert.Equal(
                firstDerivativeEtag,
                await GetDerivativeEtagAsync(producer.Services, competed.MediaId));

            await secondWorker.StopAsync(CancellationToken.None);
            await firstWorker.StopAsync(CancellationToken.None);
            await WaitForProcessingConsumerCountAsync(settings, expectedCount: 0);
            var interrupted = await ArrangeMediaAsync(
                producer.Services,
                MediaStatus.Processing,
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

            using var restartedWorker = BuildHost(settings, workerEnabled: true);
            await restartedWorker.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await WaitForStateAsync(
                    interrupted.MediaId,
                    MediaStatus.Ready,
                    attemptCount: 2);

                var storageOutage = await ArrangeMediaAsync(
                    producer.Services,
                    MediaStatus.ProcessingPending);
                await _minio.SuspendAppAccessAsync(
                    TestContext.Current.CancellationToken);
                minioAccessSuspended = true;
                await StageAsync(producer.Services, storageOutage.MediaId);
                await WaitForStateAsync(
                    storageOutage.MediaId,
                    MediaStatus.ProcessingPending,
                    attemptCount: 1,
                    requireNextAttempt: true);

                await _minio.RestoreAppAccessAsync(
                    TestContext.Current.CancellationToken);
                minioAccessSuspended = false;
                await WaitForObjectAsync(
                    producer.Services,
                    MediaObjectKey.Source(storageOutage.MediaId, revision: 1));
                await MakeRetryDueAsync(storageOutage.MediaId);
                await WaitForStateAsync(
                    storageOutage.MediaId,
                    MediaStatus.Ready,
                    attemptCount: 2);
                Assert.NotNull(await GetDerivativeEtagAsync(
                    producer.Services,
                    storageOutage.MediaId));
            }
            finally
            {
                await restartedWorker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (minioAccessSuspended)
            {
                await _minio.RestoreAppAccessAsync(CancellationToken.None);
            }

            await producer.StopAsync(CancellationToken.None);
            await secondWorker.StopAsync(CancellationToken.None);
            await firstWorker.StopAsync(CancellationToken.None);
        }
    }

    private IHost BuildHost(
        IReadOnlyDictionary<string, string?> baseSettings,
        bool workerEnabled)
    {
        var settings = new Dictionary<string, string?>(baseSettings)
        {
            ["Media:Worker:Enabled"] = workerEnabled.ToString(),
        };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_postgres.ConnectionString));
        builder.Services.AddMediaModule(
            builder.Configuration,
            workerEnabled ? MediaHostRole.Worker : MediaHostRole.Web);
        builder.Services.AddNotificationsModule(
            builder.Configuration,
            registration => registration.AddMediaConsumers(builder.Configuration));
        return builder.Build();
    }

    private IReadOnlyDictionary<string, string?> BuildSettings()
    {
        var rabbitMq = new Uri(_rabbitMq.Container.GetConnectionString());
        return new Dictionary<string, string?>
        {
            ["Messaging:RabbitMq:Enabled"] = "true",
            ["Messaging:RabbitMq:Host"] = rabbitMq.Host,
            ["Messaging:RabbitMq:Port"] = rabbitMq.Port.ToString(),
            ["Messaging:RabbitMq:VirtualHost"] = "/",
            ["Messaging:RabbitMq:Username"] = RabbitMqFixture.Username,
            ["Messaging:RabbitMq:Password"] = RabbitMqFixture.Password,
            ["Messaging:RabbitMq:EndpointPrefix"] =
                $"indiefi-media-{Guid.NewGuid():N}"[..45],
            ["Media:ObjectStorage:Enabled"] = "true",
            ["Media:ObjectStorage:ServiceUrl"] = _minio.ServiceUrl,
            ["Media:ObjectStorage:Region"] = "us-east-1",
            ["Media:ObjectStorage:BucketName"] = MinioFixture.BucketName,
            ["Media:ObjectStorage:AccessKeyId"] = MinioFixture.AppAccessKey,
            ["Media:ObjectStorage:SecretAccessKey"] = MinioFixture.AppSecretKey,
            ["Media:ObjectStorage:OperationTimeout"] = "00:00:02",
            ["Media:ObjectStorage:ForcePathStyle"] = "true",
            ["Media:Worker:RecoveryInterval"] = "00:00:01",
            ["Media:Worker:RecoveryBatchSize"] = "20",
            ["Media:Worker:CleanupInterval"] = "01:00:00",
            ["Media:Worker:CleanupBatchSize"] = "20",
            ["Media:Worker:ObservationInterval"] = "00:10:00",
        };
    }

    private async Task<ArrangedMedia> ArrangeMediaAsync(
        IServiceProvider services,
        MediaStatus status,
        DateTimeOffset? leaseExpiresAt = null)
    {
        var source = EncodePng(width: 80, height: 45);
        var mediaId = Guid.NewGuid();
        var metadata = await UploadSourceAsync(services, mediaId, source);
        var now = PostgreSqlTimestamp(DateTimeOffset.UtcNow);

        await using var db = _postgres.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Worker integration {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.MediaAssets.Add(new MediaAsset
        {
            Id = mediaId,
            OwnerUserId = owner.Id,
            GameId = game.Id,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = source.LongLength,
            SourceContentType = metadata.ContentType,
            SourceBytes = metadata.ContentLength,
            SourceETag = metadata.ETag,
            ProcessingAttemptCount = status == MediaStatus.Processing ? 1 : 0,
            ProcessingLeaseId = status == MediaStatus.Processing
                ? Guid.NewGuid()
                : null,
            ProcessingLeaseExpiresAt = status == MediaStatus.Processing
                ? leaseExpiresAt
                : null,
            CreatedAt = now,
            UpdatedAt = now,
            StatusChangedAt = now,
            UploadExpiresAt = now.AddHours(24),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new ArrangedMedia(
            mediaId,
            MediaProcessingEventId.Create(mediaId, revision: 1));
    }

    private async Task<ObjectStorageMetadata> UploadSourceAsync(
        IServiceProvider services,
        Guid mediaId,
        byte[] content)
    {
        var storage = services.GetRequiredService<IMediaObjectStorage>();
        var stagingKey = MediaObjectKey.Staging(mediaId, Guid.NewGuid());
        var uploadUrl = await storage.CreateUploadUrlAsync(
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                stagingKey,
                MediaUploadKind.GameImage,
                "image/png",
                content.LongLength),
            TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        request.Content.Headers.ContentLength = content.LongLength;
        using var http = new HttpClient();
        using var response = await http.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stagingMetadata = await storage.GetMetadataAsync(
            stagingKey,
            TestContext.Current.CancellationToken);
        Assert.NotNull(stagingMetadata);
        var sourceKey = MediaObjectKey.Source(mediaId, revision: 1);
        await storage.CopyAsync(
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                stagingKey,
                sourceKey,
                stagingMetadata.ETag,
                "image/png"),
            TestContext.Current.CancellationToken);
        await storage.DeleteIfExistsAsync(
            stagingKey,
            TestContext.Current.CancellationToken);
        return Assert.IsType<ObjectStorageMetadata>(await storage.GetMetadataAsync(
            sourceKey,
            TestContext.Current.CancellationToken));
    }

    private static async Task StageAsync(
        IServiceProvider services,
        Guid mediaId)
    {
        await using var scope = services.CreateAsyncScope();
        var publisher = scope.ServiceProvider
            .GetRequiredService<IMediaProcessingPublisher>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await publisher.StageAsync(
            MediaProcessingEventId.Create(mediaId, revision: 1),
            mediaId,
            revision: 1,
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task WaitForStateAsync(
        Guid mediaId,
        MediaStatus status,
        int attemptCount,
        bool requireNextAttempt = false) =>
        await WaitUntilAsync(async () =>
        {
            await using var db = _postgres.NewDbContext();
            return await db.MediaAssets.AsNoTracking().AnyAsync(
                media => media.Id == mediaId
                    && media.Status == status
                    && media.ProcessingAttemptCount == attemptCount
                    && (!requireNextAttempt
                        || media.NextProcessingAttemptAt != null),
                TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(60));

    private async Task AssertStateAsync(
        Guid mediaId,
        MediaStatus status,
        int attemptCount)
    {
        await using var db = _postgres.NewDbContext();
        var media = await db.MediaAssets.AsNoTracking().SingleAsync(
            item => item.Id == mediaId,
            TestContext.Current.CancellationToken);
        Assert.Equal(status, media.Status);
        Assert.Equal(attemptCount, media.ProcessingAttemptCount);
    }

    private static async Task<string?> GetDerivativeEtagAsync(
        IServiceProvider services,
        Guid mediaId) =>
        (await services.GetRequiredService<IMediaObjectStorage>().GetMetadataAsync(
            MediaObjectKey.Derived(
                mediaId,
                revision: 1,
                MediaDerivedVariant.GameDisplay),
            TestContext.Current.CancellationToken))?.ETag;

    private static Task WaitForObjectAsync(
        IServiceProvider services,
        MediaObjectKey key) =>
        WaitUntilAsync(async () =>
        {
            try
            {
                return await services
                    .GetRequiredService<IMediaObjectStorage>()
                    .GetMetadataAsync(key, TestContext.Current.CancellationToken)
                    is not null;
            }
            catch (MediaObjectStorageException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(30));

    private async Task MakeRetryDueAsync(Guid mediaId)
    {
        await using var db = _postgres.NewDbContext();
        await db.MediaAssets
            .Where(media => media.Id == mediaId
                && media.Status == MediaStatus.ProcessingPending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    media => media.NextProcessingAttemptAt,
                    DateTimeOffset.UtcNow.AddSeconds(-1)),
                TestContext.Current.CancellationToken);
    }

    private async Task WaitUntilOutboxIsEmptyAsync(Guid eventId) =>
        await WaitUntilAsync(async () =>
        {
            await using var db = _postgres.NewDbContext();
            return !await db.Set<OutboxMessage>().AnyAsync(
                item => item.MessageId == eventId,
                TestContext.Current.CancellationToken);
        }, TimeSpan.FromSeconds(60));

    private async Task WaitForProcessingConsumerCountAsync(
        IReadOnlyDictionary<string, string?> settings,
        int expectedCount)
    {
        var queueName =
            $"{settings["Messaging:RabbitMq:EndpointPrefix"]}-media-processing-requested";
        await WaitUntilAsync(async () =>
        {
            var queues = await _rabbitMq.Container.ExecAsync(
                ["rabbitmqctl", "list_queues", "name", "consumers"],
                TestContext.Current.CancellationToken);
            if (queues.ExitCode != 0)
            {
                return false;
            }

            return queues.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r').Split('\t'))
                .Any(columns => columns.Length >= 2
                    && columns[0] == queueName
                    && int.TryParse(columns[1], out var count)
                    && count == expectedCount);
        }, TimeSpan.FromSeconds(30));
    }

    private async Task ResetAsync()
    {
        await using var db = _postgres.NewOwnerDbContext();
        await db.Set<OutboxMessage>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.Set<InboxState>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.Set<OutboxState>().ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.MediaUploadAttempts.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
        await db.MediaAssets.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private static byte[] EncodePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.MediumPurple);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        return data.ToArray();
    }

    private static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was not met within {timeout}.");
    }

    private sealed record ArrangedMedia(Guid MediaId, Guid EventId);
}
