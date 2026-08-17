using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class S3MediaObjectStorageTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid MediaId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid UploadAttemptId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly MediaObjectKey StagingKey =
        MediaObjectKey.Staging(MediaId, UploadAttemptId);
    private static readonly MediaObjectKey SourceKey =
        MediaObjectKey.Source(MediaId, revision: 1);
    private static readonly MediaObjectKey DerivedKey =
        MediaObjectKey.Derived(MediaId, revision: 1, MediaDerivedVariant.GameDisplay);

    [Fact]
    public async Task SignedUrls_UseExactOperationObjectHeadersAndLifetime()
    {
        using var client = new RecordingS3Client();
        var storage = CreateStorage(client, timeProvider: TimeProvider.System);

        var upload = await storage.CreateUploadUrlAsync(
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                StagingKey,
                MediaUploadKind.GameImage,
                "image/png",
                1234),
            TestContext.Current.CancellationToken);
        var download = await storage.CreateDownloadUrlAsync(
            MediaObjectAccessPolicy.CreateDownloadUrlRequest(DerivedKey),
            TestContext.Current.CancellationToken);

        Assert.Equal("storage.example.test", upload.Host);
        Assert.Equal("storage.example.test", download.Host);
        Assert.Equal(
            "/media-private/staging/11111111111141118111111111111111/" +
            "22222222222242228222222222222222",
            upload.AbsolutePath);
        Assert.Equal(
            "/media-private/derived/11111111111141118111111111111111/1/game-display.webp",
            download.AbsolutePath);
        AssertQueryLifetime(upload, TimeSpan.FromMinutes(15));
        AssertQueryLifetime(download, TimeSpan.FromMinutes(5));
        Assert.Contains(
            "content-length%3Bcontent-type%3Bhost",
            upload.Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "X-Amz-SignedHeaders=host",
            download.Query,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignedUrls_ForLocalEndpoint_KeepHttpProtocol()
    {
        using var client = new RecordingS3Client();
        var storage = CreateStorage(client, region: "us-east-1");

        var upload = await storage.CreateUploadUrlAsync(
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                StagingKey,
                MediaUploadKind.GameImage,
                "image/png",
                4),
            TestContext.Current.CancellationToken);

        Assert.Equal(Uri.UriSchemeHttp, upload.Scheme);
    }

    [Fact]
    public async Task MetadataAndRead_MapObjectPropertiesAndDisposeResponseStream()
    {
        using var client = new RecordingS3Client
        {
            MetadataResponse = CreateMetadataResponse(),
            ObjectResponse = CreateObjectResponse(),
        };
        var storage = CreateStorage(client);

        var metadata = await storage.GetMetadataAsync(
            SourceKey,
            TestContext.Current.CancellationToken);
        await using var read = await storage.OpenReadAsync(
            SourceKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new ObjectStorageMetadata(
                321,
                "\"source-etag\"",
                "image/png",
                new DateTimeOffset(2026, 8, 16, 10, 30, 0, TimeSpan.Zero)),
            metadata);
        Assert.Equal(metadata, read.Metadata);
        Assert.Equal(SourceKey.Value, client.MetadataRequest!.Key);
        Assert.Equal(SourceKey.Value, client.GetRequest!.Key);
        Assert.Equal("media-private", client.GetRequest.BucketName);

        var stream = Assert.IsType<TrackingMemoryStream>(
            client.ObjectResponse.ResponseStream);
        Assert.NotSame(stream, read.Content);
        Assert.False(stream.WasDisposed);
        await read.DisposeAsync();
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task Write_InR2Mode_UsesUnsignedPayloadCompatibilityWithoutClosingCallerStream()
    {
        using var client = new RecordingS3Client();
        var storage = CreateStorage(client, region: "auto");
        await using var content = new TrackingMemoryStream([1, 2, 3]);

        await storage.WriteAsync(
            MediaObjectAccessPolicy.CreateDerivedWriteRequest(
                DerivedKey,
                content,
                3),
            TestContext.Current.CancellationToken);

        Assert.NotNull(client.PutRequest);
        var request = client.PutRequest;
        Assert.Equal("media-private", request.BucketName);
        Assert.Equal(DerivedKey.Value, request.Key);
        Assert.Equal("image/webp", request.ContentType);
        Assert.Equal(3, request.Headers.ContentLength);
        Assert.Same(content, request.InputStream);
        Assert.False(request.AutoCloseStream);
        Assert.True(request.DisablePayloadSigning);
        Assert.True(request.DisableDefaultChecksumValidation);
        Assert.False(content.WasDisposed);
    }

    [Fact]
    public async Task Write_ForLocalS3CompatibleService_KeepsStandardPayloadSigning()
    {
        using var client = new RecordingS3Client();
        var storage = CreateStorage(client, region: "us-east-1");
        await using var content = new MemoryStream([1]);

        await storage.WriteAsync(
            MediaObjectAccessPolicy.CreateDerivedWriteRequest(DerivedKey, content, 1),
            TestContext.Current.CancellationToken);

        Assert.False(client.PutRequest!.DisablePayloadSigning);
        Assert.False(client.PutRequest.DisableDefaultChecksumValidation);
    }

    [Fact]
    public async Task CopyAndDelete_UsePrivateBucketAndSourceEtagCondition()
    {
        using var client = new RecordingS3Client();
        var storage = CreateStorage(client);

        await storage.CopyAsync(
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                StagingKey,
                SourceKey,
                "\"confirmed-etag\"",
                "image/png"),
            TestContext.Current.CancellationToken);
        await storage.DeleteIfExistsAsync(
            StagingKey,
            TestContext.Current.CancellationToken);

        Assert.NotNull(client.CopyRequest);
        var copy = client.CopyRequest;
        Assert.Equal("media-private", copy.SourceBucket);
        Assert.Equal("media-private", copy.DestinationBucket);
        Assert.Equal(StagingKey.Value, copy.SourceKey);
        Assert.Equal(SourceKey.Value, copy.DestinationKey);
        Assert.Equal("\"confirmed-etag\"", copy.ETagToMatch);
        Assert.Equal("image/png", copy.ContentType);
        Assert.Equal(S3MetadataDirective.REPLACE, copy.MetadataDirective);
        Assert.Equal("media-private", client.DeleteRequest!.BucketName);
        Assert.Equal(StagingKey.Value, client.DeleteRequest.Key);
    }

    [Fact]
    public async Task MissingObject_MetadataReturnsNullAndDeleteRemainsIdempotent()
    {
        using var client = new RecordingS3Client
        {
            MetadataException = NotFoundException(),
            DeleteException = NotFoundException(),
        };
        var storage = CreateStorage(client);

        var metadata = await storage.GetMetadataAsync(
            StagingKey,
            TestContext.Current.CancellationToken);
        await storage.DeleteIfExistsAsync(
            StagingKey,
            TestContext.Current.CancellationToken);

        Assert.Null(metadata);
    }

    [Fact]
    public async Task MissingReadAndChangedCopy_UseStableProviderIndependentFailures()
    {
        using var client = new RecordingS3Client
        {
            GetException = NotFoundException(),
            CopyException = S3Exception(
                HttpStatusCode.PreconditionFailed,
                "PreconditionFailed"),
        };
        var storage = CreateStorage(client);

        var missing = await Assert.ThrowsAsync<MediaObjectStorageException>(() =>
            storage.OpenReadAsync(
                SourceKey,
                TestContext.Current.CancellationToken));
        var changed = await Assert.ThrowsAsync<MediaObjectStorageException>(() =>
            storage.CopyAsync(
                MediaObjectAccessPolicy.CreateSourceCopyRequest(
                    StagingKey,
                    SourceKey,
                    "\"stale-etag\"",
                    "image/png"),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            MediaObjectStorageFailureCode.ObjectNotFound,
            missing.FailureCode);
        Assert.False(missing.IsTransient);
        Assert.Equal(
            MediaObjectStorageFailureCode.PreconditionFailed,
            changed.FailureCode);
        Assert.False(changed.IsTransient);
        Assert.DoesNotContain(SourceKey.Value, missing.Message);
        Assert.DoesNotContain(StagingKey.Value, changed.Message);
    }

    [Fact]
    public async Task MissingBucket_IsUnavailableInsteadOfMissingObject()
    {
        using var client = new RecordingS3Client
        {
            MetadataException = S3Exception(
                HttpStatusCode.NotFound,
                "NoSuchBucket"),
        };
        var storage = CreateStorage(client);

        var exception = await Assert.ThrowsAsync<MediaObjectStorageException>(() =>
            storage.GetMetadataAsync(
                SourceKey,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            MediaObjectStorageFailureCode.Unavailable,
            exception.FailureCode);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task OperationTimeout_IsBoundedAndCallerCancellationRemainsCancellation()
    {
        using var client = new RecordingS3Client
        {
            WaitForMetadataCancellation = true,
        };
        var storage = CreateStorage(
            client,
            operationTimeout: TimeSpan.FromMilliseconds(25));

        var timeout = await Assert.ThrowsAsync<MediaObjectStorageException>(() =>
            storage.GetMetadataAsync(SourceKey, CancellationToken.None));

        Assert.Equal(MediaObjectStorageFailureCode.TimedOut, timeout.FailureCode);
        Assert.True(timeout.IsTransient);

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            storage.GetMetadataAsync(SourceKey, callerCancellation.Token));
    }

    [Fact]
    public async Task OpenRead_StreamCannotOutliveOperationDeadline()
    {
        using var client = new RecordingS3Client
        {
            ObjectResponse = CreateObjectResponse(
                new CancellationBlockingStream()),
        };
        var storage = CreateStorage(
            client,
            operationTimeout: TimeSpan.FromMilliseconds(25));
        await using var result = await storage.OpenReadAsync(
            SourceKey,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MediaObjectStorageException>(
            async () =>
            {
                var buffer = new byte[1];
                _ = await result.Content.ReadAsync(
                    buffer,
                    CancellationToken.None);
            });

        Assert.Equal(
            MediaObjectStorageFailureCode.TimedOut,
            exception.FailureCode);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task HealthCheck_UsesBucketHeadAndReportsStableFailureCode()
    {
        using var healthyClient = new RecordingS3Client();
        var healthyCheck = new MediaObjectStorageHealthCheck(
            CreateStorage(healthyClient));

        var healthy = await healthyCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal("media-private", healthyClient.HeadBucketRequest!.BucketName);

        using var deniedClient = new RecordingS3Client
        {
            HeadBucketException = S3Exception(
                HttpStatusCode.Forbidden,
                "AccessDenied"),
        };
        var deniedCheck = new MediaObjectStorageHealthCheck(
            CreateStorage(deniedClient));

        var denied = await deniedCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, denied.Status);
        var exception = Assert.IsType<MediaObjectStorageException>(
            denied.Exception);
        Assert.Equal(
            MediaObjectStorageFailureCode.AccessDenied,
            exception.FailureCode);
        Assert.Equal("AccessDenied", denied.Data["failure_code"]);
        Assert.DoesNotContain("media-private", denied.Description);

        using var timedOutClient = new RecordingS3Client
        {
            WaitForHeadBucketCancellation = true,
        };
        var timedOutCheck = new MediaObjectStorageHealthCheck(
            CreateStorage(
                timedOutClient,
                operationTimeout: TimeSpan.FromMilliseconds(25)));

        var timedOut = await timedOutCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, timedOut.Status);
        Assert.Equal("TimedOut", timedOut.Data["failure_code"]);
    }

    [Fact]
    public void AddMediaModule_WhenEnabled_RegistersConfiguredS3Storage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(
            new TestHostEnvironment(Environments.Production));
        services.AddMediaModule(
            Configuration(enabled: true),
            MediaHostRole.Web);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IAmazonS3>();
        var config = Assert.IsType<AmazonS3Config>(client.Config);
        Assert.Equal(
            new Uri("https://account-id.r2.cloudflarestorage.com"),
            new Uri(config.ServiceURL));
        Assert.Equal("auto", config.AuthenticationRegion);
        Assert.False(config.ForcePathStyle);
        Assert.Equal(SigningAlgorithm.HmacSHA256, config.SignatureMethod);
        Assert.Equal(TimeSpan.FromSeconds(10), config.Timeout);
        Assert.Same(
            provider.GetRequiredService<S3MediaObjectStorage>(),
            provider.GetRequiredService<IMediaObjectStorage>());
        var healthChecks = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;
        Assert.Contains(
            healthChecks,
            registration => registration.Name == "media-object-storage");
    }

    [Fact]
    public void AddMediaModule_WhenDisabled_DoesNotConstructStorageClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(
            new TestHostEnvironment(Environments.Production));
        services.AddMediaModule(
            Configuration(enabled: false),
            MediaHostRole.Web);
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IAmazonS3>());
        Assert.Null(provider.GetService<IMediaObjectStorage>());
        var healthChecks = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;
        Assert.DoesNotContain(
            healthChecks,
            registration => registration.Name == "media-object-storage");
    }

    private static S3MediaObjectStorage CreateStorage(
        IAmazonS3 client,
        string region = "auto",
        TimeProvider? timeProvider = null,
        TimeSpan? operationTimeout = null)
    {
        return new S3MediaObjectStorage(
            client,
            Options.Create(new MediaObjectStorageOptions
            {
                Enabled = true,
                ServiceUrl = region == "auto"
                    ? "https://storage.example.test"
                    : "http://127.0.0.1:9000",
                BucketName = "media-private",
                Region = region,
                OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10),
            }),
            timeProvider ?? new FixedTimeProvider(Now));
    }

    private static void AssertQueryLifetime(Uri uri, TimeSpan expected)
    {
        var values = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => part.Length == 2 ? Uri.UnescapeDataString(part[1]) : string.Empty,
                StringComparer.OrdinalIgnoreCase);
        var lifetime = int.Parse(
            values["X-Amz-Expires"],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(
            lifetime,
            (int)expected.TotalSeconds - 2,
            (int)expected.TotalSeconds);
    }

    private static GetObjectMetadataResponse CreateMetadataResponse()
    {
        var response = new GetObjectMetadataResponse
        {
            ETag = "\"source-etag\"",
            LastModified = new DateTime(2026, 8, 16, 10, 30, 0, DateTimeKind.Utc),
        };
        response.Headers.ContentLength = 321;
        response.Headers.ContentType = "image/png";
        return response;
    }

    private static GetObjectResponse CreateObjectResponse(Stream? content = null)
    {
        var response = new GetObjectResponse
        {
            ETag = "\"source-etag\"",
            LastModified = new DateTime(2026, 8, 16, 10, 30, 0, DateTimeKind.Utc),
            ResponseStream = content ?? new TrackingMemoryStream([1, 2, 3]),
        };
        response.Headers.ContentLength = 321;
        response.Headers.ContentType = "image/png";
        return response;
    }

    private static AmazonS3Exception NotFoundException() =>
        S3Exception(HttpStatusCode.NotFound, "NoSuchKey");

    private static AmazonS3Exception S3Exception(
        HttpStatusCode statusCode,
        string errorCode) => new("Object storage request failed.")
        {
            StatusCode = statusCode,
            ErrorCode = errorCode,
        };

    private static IConfiguration Configuration(bool enabled)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Media:ObjectStorage:Enabled"] = enabled.ToString(),
                ["Media:ObjectStorage:ServiceUrl"] =
                    enabled ? "https://account-id.r2.cloudflarestorage.com" : string.Empty,
                ["Media:ObjectStorage:Region"] = "auto",
                ["Media:ObjectStorage:BucketName"] = enabled ? "media-private" : string.Empty,
                ["Media:ObjectStorage:AccessKeyId"] = enabled ? "access-key" : string.Empty,
                ["Media:ObjectStorage:SecretAccessKey"] = enabled ? "secret-key" : string.Empty,
                ["Media:ObjectStorage:OperationTimeout"] = "00:00:10",
                ["Media:ObjectStorage:ForcePathStyle"] = "false",
            })
            .Build();
    }

    private sealed class RecordingS3Client : AmazonS3Client
    {
        public RecordingS3Client()
            : base(
                new BasicAWSCredentials("test-access", "test-secret"),
                new AmazonS3Config
                {
                    ServiceURL = "https://storage.example.test",
                    AuthenticationRegion = "auto",
                    ForcePathStyle = true,
                })
        {
        }

        public GetObjectMetadataRequest? MetadataRequest { get; private set; }
        public GetObjectRequest? GetRequest { get; private set; }
        public PutObjectRequest? PutRequest { get; private set; }
        public CopyObjectRequest? CopyRequest { get; private set; }
        public DeleteObjectRequest? DeleteRequest { get; private set; }
        public HeadBucketRequest? HeadBucketRequest { get; private set; }
        public GetObjectMetadataResponse MetadataResponse { get; set; } =
            CreateMetadataResponse();
        public GetObjectResponse ObjectResponse { get; set; } = CreateObjectResponse();
        public AmazonS3Exception? MetadataException { get; set; }
        public AmazonS3Exception? GetException { get; set; }
        public AmazonS3Exception? CopyException { get; set; }
        public AmazonS3Exception? DeleteException { get; set; }
        public AmazonS3Exception? HeadBucketException { get; set; }
        public bool WaitForMetadataCancellation { get; set; }
        public bool WaitForHeadBucketCancellation { get; set; }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
            GetObjectMetadataRequest request,
            CancellationToken cancellationToken = default)
        {
            MetadataRequest = request;
            if (WaitForMetadataCancellation)
            {
                return WaitUntilCanceledAsync<GetObjectMetadataResponse>(
                    cancellationToken);
            }

            return MetadataException is null
                ? Task.FromResult(MetadataResponse)
                : Task.FromException<GetObjectMetadataResponse>(MetadataException);
        }

        public override Task<GetObjectResponse> GetObjectAsync(
            GetObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            GetRequest = request;
            return GetException is null
                ? Task.FromResult(ObjectResponse)
                : Task.FromException<GetObjectResponse>(GetException);
        }

        public override Task<PutObjectResponse> PutObjectAsync(
            PutObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            PutRequest = request;
            return Task.FromResult(new PutObjectResponse());
        }

        public override Task<CopyObjectResponse> CopyObjectAsync(
            CopyObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            CopyRequest = request;
            return CopyException is null
                ? Task.FromResult(new CopyObjectResponse())
                : Task.FromException<CopyObjectResponse>(CopyException);
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(
            DeleteObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            DeleteRequest = request;
            return DeleteException is null
                ? Task.FromResult(new DeleteObjectResponse())
                : Task.FromException<DeleteObjectResponse>(DeleteException);
        }

        public override Task<HeadBucketResponse> HeadBucketAsync(
            HeadBucketRequest request,
            CancellationToken cancellationToken = default)
        {
            HeadBucketRequest = request;
            if (WaitForHeadBucketCancellation)
            {
                return WaitUntilCanceledAsync<HeadBucketResponse>(
                    cancellationToken);
            }

            return HeadBucketException is null
                ? Task.FromResult(new HeadBucketResponse())
                : Task.FromException<HeadBucketResponse>(HeadBucketException);
        }

        private static async Task<T> WaitUntilCanceledAsync<T>(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation delay unexpectedly ended.");
        }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public TrackingMemoryStream(byte[] buffer)
            : base(buffer)
        {
        }

        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationBlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _value;

        public FixedTimeProvider(DateTimeOffset value)
        {
            _value = value;
        }

        public override DateTimeOffset GetUtcNow() => _value;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "IndieFi.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
