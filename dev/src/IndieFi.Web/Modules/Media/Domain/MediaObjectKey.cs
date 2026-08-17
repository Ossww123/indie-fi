using System.Globalization;

namespace IndieFi.Web.Modules.Media.Domain;

public enum MediaObjectKeyKind
{
    Staging,
    Source,
    Derived,
}

public enum MediaDerivedVariant
{
    GameDisplay,
    GameList,
    ProfileDisplay,
    ProfileThumbnail,
}

public sealed record MediaObjectKey
{
    private const string WebpExtension = ".webp";

    private MediaObjectKey(
        string value,
        MediaObjectKeyKind kind,
        Guid mediaId,
        Guid? uploadAttemptId = null,
        int? revision = null,
        MediaDerivedVariant? variant = null)
    {
        Value = value;
        Kind = kind;
        MediaId = mediaId;
        UploadAttemptId = uploadAttemptId;
        Revision = revision;
        Variant = variant;
    }

    public string Value { get; }
    public MediaObjectKeyKind Kind { get; }
    public Guid MediaId { get; }
    public Guid? UploadAttemptId { get; }
    public int? Revision { get; }
    public MediaDerivedVariant? Variant { get; }

    public static MediaObjectKey Staging(Guid mediaId, Guid uploadAttemptId)
    {
        RequireIdentifier(mediaId, nameof(mediaId));
        RequireIdentifier(uploadAttemptId, nameof(uploadAttemptId));
        return new MediaObjectKey(
            $"staging/{Format(mediaId)}/{Format(uploadAttemptId)}",
            MediaObjectKeyKind.Staging,
            mediaId,
            uploadAttemptId: uploadAttemptId);
    }

    public static MediaObjectKey Source(Guid mediaId, int revision)
    {
        RequireIdentifier(mediaId, nameof(mediaId));
        RequireRevision(revision);
        return new MediaObjectKey(
            $"sources/{Format(mediaId)}/{revision.ToString(CultureInfo.InvariantCulture)}",
            MediaObjectKeyKind.Source,
            mediaId,
            revision: revision);
    }

    public static MediaObjectKey Derived(
        Guid mediaId,
        int revision,
        MediaDerivedVariant variant)
    {
        RequireIdentifier(mediaId, nameof(mediaId));
        RequireRevision(revision);
        var variantCode = VariantCode(variant);
        return new MediaObjectKey(
            $"derived/{Format(mediaId)}/{revision.ToString(CultureInfo.InvariantCulture)}/" +
            $"{variantCode}{WebpExtension}",
            MediaObjectKeyKind.Derived,
            mediaId,
            revision: revision,
            variant: variant);
    }

    public static MediaObjectKey Parse(string value)
    {
        if (!TryParse(value, out var key))
        {
            throw new FormatException("The media object key does not use a supported canonical format.");
        }

        return key;
    }

    public static bool TryParse(string? value, out MediaObjectKey key)
    {
        key = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 160)
        {
            return false;
        }

        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Length == 3
            && segments[0] == "staging"
            && TryCanonicalIdentifier(segments[1], out var stagingMediaId)
            && TryCanonicalIdentifier(segments[2], out var uploadAttemptId))
        {
            key = Staging(stagingMediaId, uploadAttemptId);
            return true;
        }

        if (segments.Length == 3
            && segments[0] == "sources"
            && TryCanonicalIdentifier(segments[1], out var sourceMediaId)
            && TryCanonicalRevision(segments[2], out var sourceRevision))
        {
            key = Source(sourceMediaId, sourceRevision);
            return true;
        }

        if (segments.Length == 4
            && segments[0] == "derived"
            && TryCanonicalIdentifier(segments[1], out var derivedMediaId)
            && TryCanonicalRevision(segments[2], out var derivedRevision)
            && TryVariant(segments[3], out var variant))
        {
            key = Derived(derivedMediaId, derivedRevision, variant);
            return true;
        }

        return false;
    }

    public override string ToString() => Value;

    public static string VariantCode(MediaDerivedVariant variant) => variant switch
    {
        MediaDerivedVariant.GameDisplay => "game-display",
        MediaDerivedVariant.GameList => "game-list",
        MediaDerivedVariant.ProfileDisplay => "profile-display",
        MediaDerivedVariant.ProfileThumbnail => "profile-thumbnail",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static bool TryVariant(
        string fileName,
        out MediaDerivedVariant variant)
    {
        variant = default;
        if (!fileName.EndsWith(WebpExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var code = fileName[..^WebpExtension.Length];
        variant = code switch
        {
            "game-display" => MediaDerivedVariant.GameDisplay,
            "game-list" => MediaDerivedVariant.GameList,
            "profile-display" => MediaDerivedVariant.ProfileDisplay,
            "profile-thumbnail" => MediaDerivedVariant.ProfileThumbnail,
            _ => default,
        };
        return code == VariantCode(variant);
    }

    private static bool TryCanonicalIdentifier(string value, out Guid identifier)
    {
        return Guid.TryParseExact(value, "N", out identifier)
            && identifier != Guid.Empty
            && value == Format(identifier);
    }

    private static bool TryCanonicalRevision(string value, out int revision)
    {
        return int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out revision)
            && revision > 0
            && value == revision.ToString(CultureInfo.InvariantCulture);
    }

    private static string Format(Guid identifier) => identifier.ToString("N");

    private static void RequireIdentifier(Guid identifier, string parameterName)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException(
                "Media object identifiers must not be empty.",
                parameterName);
        }
    }

    private static void RequireRevision(int revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "A media object revision must be positive.");
        }
    }
}
