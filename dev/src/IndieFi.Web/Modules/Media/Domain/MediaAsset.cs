namespace IndieFi.Web.Modules.Media.Domain;

/// <summary>
/// One immutable media candidate and its lifecycle metadata. Object keys are
/// derived from <see cref="Id"/>, upload attempt identifiers, and
/// <see cref="Revision"/>; descriptive user data is never stored in a key.
/// </summary>
public sealed class MediaAsset
{
    public Guid Id { get; set; }
    public long OwnerUserId { get; set; }
    public long? GameId { get; set; }
    public long? ProfileUserId { get; set; }
    public MediaAttachmentKind AttachmentKind { get; set; }
    public MediaStatus Status { get; set; } = MediaStatus.UploadPending;

    public string DeclaredContentType { get; set; } = string.Empty;
    public long DeclaredBytes { get; set; }
    public int Revision { get; set; } = 1;

    public string? SourceContentType { get; set; }
    public long? SourceBytes { get; set; }
    public string? SourceETag { get; set; }

    public string? DetectedContentType { get; set; }
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }
    public int? FrameCount { get; set; }

    public int ProcessingAttemptCount { get; set; }
    public Guid? ProcessingLeaseId { get; set; }
    public DateTimeOffset? ProcessingLeaseExpiresAt { get; set; }
    public DateTimeOffset? NextProcessingAttemptAt { get; set; }
    public string? FailureCode { get; set; }

    public bool IsCurrent { get; set; }
    public int? SortOrder { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; }
    public DateTimeOffset UploadExpiresAt { get; set; }
    public DateTimeOffset? SourceDeletedAt { get; set; }
    public DateTimeOffset? DeletionCompletedAt { get; set; }
    public DateTimeOffset? TombstoneExpiresAt { get; set; }

    public ICollection<MediaUploadAttempt> UploadAttempts { get; } =
        new List<MediaUploadAttempt>();
}

/// <summary>
/// One short-lived signed upload authority. Reissuing creates a new row and
/// supersedes the prior attempt instead of extending its object key.
/// </summary>
public sealed class MediaUploadAttempt
{
    public Guid Id { get; set; }
    public Guid MediaId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset UrlExpiresAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public MediaAsset Media { get; set; } = null!;
}
