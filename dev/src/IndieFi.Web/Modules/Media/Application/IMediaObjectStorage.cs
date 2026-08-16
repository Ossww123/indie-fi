using IndieFi.Web.Modules.Media.Domain;

namespace IndieFi.Web.Modules.Media.Application;

/// <summary>
/// Provider-independent access to private media objects shared by Web and Worker processes.
/// Authorization remains the caller's responsibility and must be decided from PostgreSQL state.
/// </summary>
public interface IMediaObjectStorage
{
    Task<Uri> CreateUploadUrlAsync(
        ObjectStorageUploadUrlRequest request,
        CancellationToken cancellationToken = default);

    Task<Uri> CreateDownloadUrlAsync(
        ObjectStorageDownloadUrlRequest request,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageMetadata?> GetMetadataAsync(
        MediaObjectKey objectKey,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageReadResult> OpenReadAsync(
        MediaObjectKey objectKey,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        ObjectStorageWriteRequest request,
        CancellationToken cancellationToken = default);

    Task CopyAsync(
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an object when present. A missing object is treated as an already completed delete.
    /// </summary>
    Task DeleteIfExistsAsync(
        MediaObjectKey objectKey,
        CancellationToken cancellationToken = default);
}

public sealed record ObjectStorageUploadUrlRequest
{
    internal ObjectStorageUploadUrlRequest(
        MediaObjectKey objectKey,
        MediaUploadKind uploadKind,
        string contentType,
        long contentLength)
    {
        ObjectKey = objectKey;
        UploadKind = uploadKind;
        ContentType = contentType;
        ContentLength = contentLength;
    }

    public MediaObjectKey ObjectKey { get; }
    public MediaUploadKind UploadKind { get; }
    public string ContentType { get; }
    public long ContentLength { get; }
}

public sealed record ObjectStorageDownloadUrlRequest
{
    internal ObjectStorageDownloadUrlRequest(MediaObjectKey objectKey)
    {
        ObjectKey = objectKey;
    }

    public MediaObjectKey ObjectKey { get; }
}

public sealed record ObjectStorageMetadata(
    long ContentLength,
    string ETag,
    string? ContentType,
    DateTimeOffset LastModified);

public sealed record ObjectStorageWriteRequest
{
    internal ObjectStorageWriteRequest(
        MediaObjectKey objectKey,
        Stream content,
        string contentType,
        long contentLength)
    {
        ObjectKey = objectKey;
        Content = content;
        ContentType = contentType;
        ContentLength = contentLength;
    }

    public MediaObjectKey ObjectKey { get; }
    public Stream Content { get; }
    public string ContentType { get; }
    public long ContentLength { get; }
}

public sealed record ObjectStorageCopyRequest
{
    internal ObjectStorageCopyRequest(
        MediaObjectKey sourceObjectKey,
        MediaObjectKey destinationObjectKey,
        string sourceETag,
        string contentType)
    {
        SourceObjectKey = sourceObjectKey;
        DestinationObjectKey = destinationObjectKey;
        SourceETag = sourceETag;
        ContentType = contentType;
    }

    public MediaObjectKey SourceObjectKey { get; }
    public MediaObjectKey DestinationObjectKey { get; }
    public string SourceETag { get; }
    public string ContentType { get; }
}

public sealed class ObjectStorageReadResult : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public ObjectStorageReadResult(
        Stream content,
        ObjectStorageMetadata metadata,
        IDisposable? owner = null)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _owner = owner;
    }

    public Stream Content { get; }

    public ObjectStorageMetadata Metadata { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Content.DisposeAsync();
        }
        finally
        {
            _owner?.Dispose();
        }
    }
}
