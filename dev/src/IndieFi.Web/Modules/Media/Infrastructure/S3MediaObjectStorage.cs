using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using IndieFi.Web.Infrastructure.Observability;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class S3MediaObjectStorage : IMediaObjectStorage
{
    private readonly IAmazonS3 _client;
    private readonly TimeProvider _timeProvider;
    private readonly string _bucketName;
    private readonly TimeSpan _operationTimeout;
    private readonly Protocol _signedUrlProtocol;
    private readonly bool _useR2UploadCompatibility;

    public S3MediaObjectStorage(
        IAmazonS3 client,
        IOptions<MediaObjectStorageOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _client = client;
        _timeProvider = timeProvider;
        _bucketName = options.Value.BucketName;
        _operationTimeout = options.Value.OperationTimeout;
        _signedUrlProtocol = new Uri(options.Value.ServiceUrl).Scheme == Uri.UriSchemeHttp
            ? Protocol.HTTP
            : Protocol.HTTPS;
        _useR2UploadCompatibility = string.Equals(
            options.Value.Region,
            "auto",
            StringComparison.Ordinal);
    }

    public Task<Uri> CreateUploadUrlAsync(
        ObjectStorageUploadUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        MediaObjectAccessPolicy.ValidateUploadUrlRequest(request);
        var signingRequest = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = request.ObjectKey.Value,
            Verb = HttpVerb.PUT,
            ContentType = request.ContentType,
            Expires = ExpiresAt(MediaObjectAccessPolicy.UploadUrlLifetime),
            Protocol = _signedUrlProtocol,
        };
        signingRequest.Headers.ContentLength = request.ContentLength;
        return CreateSignedUriAsync(signingRequest, cancellationToken);
    }

    public Task<Uri> CreateDownloadUrlAsync(
        ObjectStorageDownloadUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        MediaObjectAccessPolicy.ValidateDownloadUrlRequest(request);
        return CreateSignedUriAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = request.ObjectKey.Value,
                Verb = HttpVerb.GET,
                Expires = ExpiresAt(MediaObjectAccessPolicy.DownloadUrlLifetime),
                Protocol = _signedUrlProtocol,
            },
            cancellationToken);
    }

    public async Task<ObjectStorageMetadata?> GetMetadataAsync(
        MediaObjectKey objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        try
        {
            var response = await ExecuteAsync(
                "get_metadata",
                token => _client.GetObjectMetadataAsync(
                    new GetObjectMetadataRequest
                    {
                        BucketName = _bucketName,
                        Key = objectKey.Value,
                    },
                    token),
                ObjectNotFoundBehavior.AnyNotFound,
                cancellationToken);
            return ToMetadata(response);
        }
        catch (MediaObjectStorageException exception)
            when (exception.FailureCode ==
                MediaObjectStorageFailureCode.ObjectNotFound)
        {
            return null;
        }
    }

    public async Task<ObjectStorageReadResult> OpenReadAsync(
        MediaObjectKey objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        var timeout = CreateOperationCancellation(cancellationToken);
        GetObjectResponse? response = null;
        try
        {
            response = await ExecuteCoreAsync(
                "open_read",
                token => _client.GetObjectAsync(
                    new GetObjectRequest
                    {
                        BucketName = _bucketName,
                        Key = objectKey.Value,
                    },
                    token),
                ObjectNotFoundBehavior.AnyNotFound,
                cancellationToken,
                timeout.Token);
            return new ObjectStorageReadResult(
                new DeadlineReadStream(
                    response.ResponseStream,
                    timeout,
                    cancellationToken),
                ToMetadata(response),
                response);
        }
        catch
        {
            timeout.Dispose();
            response?.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(
        ObjectStorageWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        MediaObjectAccessPolicy.ValidateDerivedWriteRequest(request);
        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = request.ObjectKey.Value,
            InputStream = request.Content,
            ContentType = request.ContentType,
            AutoCloseStream = false,
            DisablePayloadSigning = _useR2UploadCompatibility,
            DisableDefaultChecksumValidation = _useR2UploadCompatibility,
        };
        putRequest.Headers.ContentLength = request.ContentLength;
        await ExecuteAsync(
            "write",
            token => _client.PutObjectAsync(putRequest, token),
            ObjectNotFoundBehavior.None,
            cancellationToken);
    }

    public async Task CopyAsync(
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        MediaObjectAccessPolicy.ValidateSourceCopyRequest(request);
        await ExecuteAsync(
            "copy",
            token => _client.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = _bucketName,
                    SourceKey = request.SourceObjectKey.Value,
                    DestinationBucket = _bucketName,
                    DestinationKey = request.DestinationObjectKey.Value,
                    ETagToMatch = request.SourceETag,
                    ContentType = request.ContentType,
                    MetadataDirective = S3MetadataDirective.REPLACE,
                },
                token),
            ObjectNotFoundBehavior.AnyNotFound,
            cancellationToken);
    }

    public async Task DeleteIfExistsAsync(
        MediaObjectKey objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectKey);
        try
        {
            await ExecuteAsync(
                "delete",
                token => _client.DeleteObjectAsync(
                    new DeleteObjectRequest
                    {
                        BucketName = _bucketName,
                        Key = objectKey.Value,
                    },
                    token),
                ObjectNotFoundBehavior.ExplicitObjectOnly,
                cancellationToken);
        }
        catch (MediaObjectStorageException exception)
            when (exception.FailureCode ==
                MediaObjectStorageFailureCode.ObjectNotFound)
        {
            // A missing object is the desired end state for a retryable delete.
        }
    }

    internal async Task CheckReadyAsync(
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            "check_ready",
            token => _client.HeadBucketAsync(
                new HeadBucketRequest
                {
                    BucketName = _bucketName,
                },
                token),
            ObjectNotFoundBehavior.None,
            cancellationToken);
    }

    private async Task<Uri> CreateSignedUriAsync(
        GetPreSignedUrlRequest request,
        CancellationToken cancellationToken)
    {
        var operation = request.Verb == HttpVerb.PUT
            ? "presign_upload"
            : "presign_download";
        using var activity = ExternalOperationTelemetry.Start("r2", operation);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await _client.GetPreSignedURLAsync(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw Failure(
                    MediaObjectStorageFailureCode.InvalidResponse,
                    "Media object storage returned an invalid response.");
            }

            return uri;
        }
        catch (Exception exception)
        {
            ExternalOperationTelemetry.RecordFailure(activity, exception);
            throw;
        }
    }

    private DateTime ExpiresAt(TimeSpan lifetime)
    {
        return _timeProvider.GetUtcNow().Add(lifetime).UtcDateTime;
    }

    private static ObjectStorageMetadata ToMetadata(GetObjectMetadataResponse response)
    {
        return new ObjectStorageMetadata(
            response.Headers.ContentLength,
            response.ETag,
            response.Headers.ContentType,
            ToUtcTimestamp(response.LastModified));
    }

    private static ObjectStorageMetadata ToMetadata(GetObjectResponse response)
    {
        return new ObjectStorageMetadata(
            response.Headers.ContentLength,
            response.ETag,
            response.Headers.ContentType,
            ToUtcTimestamp(response.LastModified));
    }

    private static DateTimeOffset ToUtcTimestamp(DateTime? value)
    {
        if (value is null)
        {
            throw Failure(
                MediaObjectStorageFailureCode.InvalidResponse,
                "Media object storage returned an invalid response.");
        }

        return new DateTimeOffset(value.Value.ToUniversalTime());
    }

    private async Task<TResponse> ExecuteAsync<TResponse>(
        string operationName,
        Func<CancellationToken, Task<TResponse>> operation,
        ObjectNotFoundBehavior objectNotFoundBehavior,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateOperationCancellation(cancellationToken);
        return await ExecuteCoreAsync(
            operationName,
            operation,
            objectNotFoundBehavior,
            cancellationToken,
            timeout.Token);
    }

    private async Task<TResponse> ExecuteCoreAsync<TResponse>(
        string operationName,
        Func<CancellationToken, Task<TResponse>> operation,
        ObjectNotFoundBehavior objectNotFoundBehavior,
        CancellationToken cancellationToken,
        CancellationToken operationCancellationToken)
    {
        using var activity = ExternalOperationTelemetry.Start(
            "r2",
            operationName);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await operation(operationCancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            ExternalOperationTelemetry.RecordFailure(activity, exception);
            throw Failure(
                MediaObjectStorageFailureCode.TimedOut,
                "Media object storage did not respond before the operation timeout.",
                exception);
        }
        catch (AmazonS3Exception exception)
        {
            ExternalOperationTelemetry.RecordFailure(activity, exception);
            throw MapS3Failure(exception, objectNotFoundBehavior);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException)
        {
            ExternalOperationTelemetry.RecordFailure(activity, exception);
            throw Failure(
                MediaObjectStorageFailureCode.Unavailable,
                "Media object storage is unavailable.",
                exception);
        }
    }

    private CancellationTokenSource CreateOperationCancellation(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        return timeout;
    }

    private static MediaObjectStorageException MapS3Failure(
        AmazonS3Exception exception,
        ObjectNotFoundBehavior objectNotFoundBehavior)
    {
        if (IsObjectNotFound(exception, objectNotFoundBehavior))
        {
            return Failure(
                MediaObjectStorageFailureCode.ObjectNotFound,
                "The media object was not found.",
                exception);
        }

        if (exception.StatusCode == HttpStatusCode.PreconditionFailed
            || HasErrorCode(exception, "PreconditionFailed"))
        {
            return Failure(
                MediaObjectStorageFailureCode.PreconditionFailed,
                "The media object changed before the operation completed.",
                exception);
        }

        if (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || HasErrorCode(exception, "AccessDenied")
            || HasErrorCode(exception, "InvalidAccessKeyId")
            || HasErrorCode(exception, "SignatureDoesNotMatch"))
        {
            return Failure(
                MediaObjectStorageFailureCode.AccessDenied,
                "Media object storage access was denied.",
                exception);
        }

        if (exception.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.GatewayTimeout
            || HasErrorCode(exception, "RequestTimeout"))
        {
            return Failure(
                MediaObjectStorageFailureCode.TimedOut,
                "Media object storage did not respond before the operation timeout.",
                exception);
        }

        return Failure(
            MediaObjectStorageFailureCode.Unavailable,
            "Media object storage is unavailable.",
            exception);
    }

    private static bool IsObjectNotFound(
        AmazonS3Exception exception,
        ObjectNotFoundBehavior behavior)
    {
        if (behavior == ObjectNotFoundBehavior.None)
        {
            return false;
        }

        var explicitObjectNotFound = HasErrorCode(exception, "NoSuchKey")
            || HasErrorCode(exception, "NotFound");
        return explicitObjectNotFound
            || behavior == ObjectNotFoundBehavior.AnyNotFound
                && exception.StatusCode == HttpStatusCode.NotFound
                && !HasErrorCode(exception, "NoSuchBucket");
    }

    private static bool HasErrorCode(
        AmazonS3Exception exception,
        string value) =>
        string.Equals(exception.ErrorCode, value, StringComparison.Ordinal);

    private static MediaObjectStorageException Failure(
        MediaObjectStorageFailureCode failureCode,
        string message,
        Exception? innerException = null) =>
        new(failureCode, message, innerException);

    private enum ObjectNotFoundBehavior
    {
        None,
        AnyNotFound,
        ExplicitObjectOnly,
    }

    private sealed class DeadlineReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationTokenSource _operationCancellation;
        private readonly CancellationToken _callerCancellation;
        private bool _disposed;

        public DeadlineReadStream(
            Stream inner,
            CancellationTokenSource operationCancellation,
            CancellationToken callerCancellation)
        {
            _inner = inner ?? throw new MediaObjectStorageException(
                MediaObjectStorageFailureCode.InvalidResponse,
                "Media object storage returned an invalid response.");
            _operationCancellation = operationCancellation;
            _callerCancellation = callerCancellation;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(
            CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(
                    buffer.AsMemory(offset, count),
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            using var linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _operationCancellation.Token);
            try
            {
                return await _inner.ReadAsync(
                    buffer,
                    linkedCancellation.Token);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested
                    && !_callerCancellation.IsCancellationRequested)
            {
                throw Failure(
                    MediaObjectStorageFailureCode.TimedOut,
                    "Media object storage did not respond before the operation timeout.",
                    exception);
            }
            catch (AmazonS3Exception exception)
            {
                throw MapS3Failure(
                    exception,
                    ObjectNotFoundBehavior.AnyNotFound);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or IOException)
            {
                throw Failure(
                    MediaObjectStorageFailureCode.Unavailable,
                    "Media object storage is unavailable.",
                    exception);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _inner.Dispose();
                _operationCancellation.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    await _inner.DisposeAsync();
                }
                finally
                {
                    _operationCancellation.Dispose();
                }
            }

            GC.SuppressFinalize(this);
        }
    }
}
