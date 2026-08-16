using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using IndieFi.Web.Modules.Media.Application;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class S3MediaObjectStorage : IMediaObjectStorage
{
    private readonly IAmazonS3 _client;
    private readonly TimeProvider _timeProvider;
    private readonly string _bucketName;
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
        _useR2UploadCompatibility = string.Equals(
            options.Value.Region,
            "auto",
            StringComparison.Ordinal);
    }

    public Task<Uri> CreateUploadUrlAsync(
        ObjectStorageUploadUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var signingRequest = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = request.ObjectKey,
            Verb = HttpVerb.PUT,
            ContentType = request.ContentType,
            Expires = ExpiresAt(request.Lifetime),
        };
        signingRequest.Headers.ContentLength = request.ContentLength;
        return CreateSignedUriAsync(signingRequest, cancellationToken);
    }

    public Task<Uri> CreateDownloadUrlAsync(
        ObjectStorageDownloadUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateSignedUriAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = request.ObjectKey,
                Verb = HttpVerb.GET,
                Expires = ExpiresAt(request.Lifetime),
            },
            cancellationToken);
    }

    public async Task<ObjectStorageMetadata?> GetMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                },
                cancellationToken);
            return ToMetadata(response);
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            return null;
        }
    }

    public async Task<ObjectStorageReadResult> OpenReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = objectKey,
            },
            cancellationToken);
        try
        {
            return new ObjectStorageReadResult(
                response.ResponseStream,
                ToMetadata(response),
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(
        ObjectStorageWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var putRequest = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = request.ObjectKey,
            InputStream = request.Content,
            ContentType = request.ContentType,
            AutoCloseStream = false,
            DisablePayloadSigning = _useR2UploadCompatibility,
            DisableDefaultChecksumValidation = _useR2UploadCompatibility,
        };
        putRequest.Headers.ContentLength = request.ContentLength;
        await _client.PutObjectAsync(putRequest, cancellationToken);
    }

    public async Task CopyAsync(
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _client.CopyObjectAsync(
            new CopyObjectRequest
            {
                SourceBucket = _bucketName,
                SourceKey = request.SourceObjectKey,
                DestinationBucket = _bucketName,
                DestinationKey = request.DestinationObjectKey,
                ETagToMatch = request.SourceETag,
                ContentType = request.ContentType,
                MetadataDirective = S3MetadataDirective.REPLACE,
            },
            cancellationToken);
    }

    public async Task DeleteIfExistsAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNotFound(exception))
        {
            // A missing object is the desired end state for a retryable delete.
        }
    }

    private async Task<Uri> CreateSignedUriAsync(
        GetPreSignedUrlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _client.GetPreSignedURLAsync(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "The object storage client returned an invalid pre-signed URL.");
        }

        return uri;
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
            throw new InvalidOperationException(
                "The object storage response did not include a last-modified timestamp.");
        }

        return new DateTimeOffset(value.Value.ToUniversalTime());
    }

    private static bool IsNotFound(AmazonS3Exception exception)
    {
        return exception.StatusCode == HttpStatusCode.NotFound
            || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal);
    }
}
