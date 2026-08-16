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
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageReadResult> OpenReadAsync(
        string objectKey,
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
        string objectKey,
        CancellationToken cancellationToken = default);
}

public sealed record ObjectStorageUploadUrlRequest(
    string ObjectKey,
    string ContentType,
    long ContentLength,
    TimeSpan Lifetime);

public sealed record ObjectStorageDownloadUrlRequest(
    string ObjectKey,
    TimeSpan Lifetime);

public sealed record ObjectStorageMetadata(
    long ContentLength,
    string ETag,
    string? ContentType,
    DateTimeOffset LastModified);

public sealed record ObjectStorageWriteRequest(
    string ObjectKey,
    Stream Content,
    string ContentType,
    long ContentLength);

public sealed record ObjectStorageCopyRequest(
    string SourceObjectKey,
    string DestinationObjectKey,
    string SourceETag,
    string ContentType);

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
