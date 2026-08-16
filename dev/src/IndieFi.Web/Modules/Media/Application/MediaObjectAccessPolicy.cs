using IndieFi.Web.Modules.Media.Domain;

namespace IndieFi.Web.Modules.Media.Application;

public enum MediaUploadKind
{
    GameImage,
    ProfileImage,
}

public static class MediaObjectAccessPolicy
{
    public const long GameImageMaximumBytes = MediaPolicy.GameImageMaximumBytes;
    public const long ProfileImageMaximumBytes = MediaPolicy.ProfileImageMaximumBytes;
    public const long DerivedImageMaximumBytes = GameImageMaximumBytes;

    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromMinutes(5);

    private static readonly string[] AllowedOriginalContentTypes =
    [
        "image/jpeg",
        "image/png",
        "image/webp",
    ];

    public static ObjectStorageUploadUrlRequest CreateUploadUrlRequest(
        MediaObjectKey stagingKey,
        MediaUploadKind uploadKind,
        string contentType,
        long contentLength)
    {
        RequireKind(stagingKey, MediaObjectKeyKind.Staging, nameof(stagingKey));
        RequireOriginalContentType(contentType);
        var maximumLength = uploadKind switch
        {
            MediaUploadKind.GameImage => GameImageMaximumBytes,
            MediaUploadKind.ProfileImage => ProfileImageMaximumBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(uploadKind)),
        };
        RequireContentLength(contentLength, maximumLength);
        return new ObjectStorageUploadUrlRequest(
            stagingKey,
            uploadKind,
            contentType,
            contentLength);
    }

    public static ObjectStorageDownloadUrlRequest CreateDownloadUrlRequest(
        MediaObjectKey derivedKey)
    {
        RequireKind(derivedKey, MediaObjectKeyKind.Derived, nameof(derivedKey));
        return new ObjectStorageDownloadUrlRequest(derivedKey);
    }

    public static ObjectStorageWriteRequest CreateDerivedWriteRequest(
        MediaObjectKey derivedKey,
        Stream content,
        long contentLength)
    {
        RequireKind(derivedKey, MediaObjectKeyKind.Derived, nameof(derivedKey));
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The derived object content stream must be readable.", nameof(content));
        }

        RequireContentLength(contentLength, DerivedImageMaximumBytes);
        return new ObjectStorageWriteRequest(
            derivedKey,
            content,
            "image/webp",
            contentLength);
    }

    public static ObjectStorageCopyRequest CreateSourceCopyRequest(
        MediaObjectKey stagingKey,
        MediaObjectKey sourceKey,
        string sourceETag,
        string contentType)
    {
        RequireKind(stagingKey, MediaObjectKeyKind.Staging, nameof(stagingKey));
        RequireKind(sourceKey, MediaObjectKeyKind.Source, nameof(sourceKey));
        if (stagingKey.MediaId != sourceKey.MediaId)
        {
            throw new ArgumentException(
                "Source copies must remain within the same media identifier.",
                nameof(sourceKey));
        }

        RequireETag(sourceETag);
        RequireOriginalContentType(contentType);
        return new ObjectStorageCopyRequest(
            stagingKey,
            sourceKey,
            sourceETag,
            contentType);
    }

    public static bool IsAllowedOriginalContentType(string? contentType)
    {
        return contentType is not null
            && AllowedOriginalContentTypes.Contains(contentType, StringComparer.Ordinal);
    }

    public static bool IsValidETag(string? value)
    {
        if (value is null
            || value.Length is < 3 or > 130
            || value[0] != '"'
            || value[^1] != '"')
        {
            return false;
        }

        return value[1..^1].All(character =>
            character is >= '!' and <= '~'
            && character is not '"' and not '\\');
    }

    internal static void ValidateUploadUrlRequest(ObjectStorageUploadUrlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKind(request.ObjectKey, MediaObjectKeyKind.Staging, nameof(request));
        RequireOriginalContentType(request.ContentType);
        var maximumLength = request.UploadKind switch
        {
            MediaUploadKind.GameImage => GameImageMaximumBytes,
            MediaUploadKind.ProfileImage => ProfileImageMaximumBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        RequireContentLength(request.ContentLength, maximumLength);
    }

    internal static void ValidateDownloadUrlRequest(ObjectStorageDownloadUrlRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKind(request.ObjectKey, MediaObjectKeyKind.Derived, nameof(request));
    }

    internal static void ValidateDerivedWriteRequest(ObjectStorageWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKind(request.ObjectKey, MediaObjectKeyKind.Derived, nameof(request));
        if (!string.Equals(request.ContentType, "image/webp", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Derived media objects must use the WebP content type.",
                nameof(request));
        }

        if (!request.Content.CanRead)
        {
            throw new ArgumentException(
                "The derived object content stream must be readable.",
                nameof(request));
        }

        RequireContentLength(request.ContentLength, DerivedImageMaximumBytes);
    }

    internal static void ValidateSourceCopyRequest(ObjectStorageCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKind(request.SourceObjectKey, MediaObjectKeyKind.Staging, nameof(request));
        RequireKind(request.DestinationObjectKey, MediaObjectKeyKind.Source, nameof(request));
        if (request.SourceObjectKey.MediaId != request.DestinationObjectKey.MediaId)
        {
            throw new ArgumentException(
                "Source copies must remain within the same media identifier.",
                nameof(request));
        }

        RequireETag(request.SourceETag);
        RequireOriginalContentType(request.ContentType);
    }

    private static void RequireOriginalContentType(string contentType)
    {
        if (!IsAllowedOriginalContentType(contentType))
        {
            throw new ArgumentException(
                "Only JPEG, PNG, or WebP image content types are allowed.",
                nameof(contentType));
        }
    }

    private static void RequireContentLength(long contentLength, long maximumLength)
    {
        if (contentLength <= 0 || contentLength > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentLength),
                $"Object content length must be between 1 and {maximumLength} bytes.");
        }
    }

    private static void RequireETag(string sourceETag)
    {
        if (!IsValidETag(sourceETag))
        {
            throw new ArgumentException(
                "The source ETag has an invalid format.",
                nameof(sourceETag));
        }
    }

    private static void RequireKind(
        MediaObjectKey key,
        MediaObjectKeyKind expected,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (key.Kind != expected)
        {
            throw new ArgumentException(
                "The media object key is not valid for this operation.",
                parameterName);
        }
    }
}
