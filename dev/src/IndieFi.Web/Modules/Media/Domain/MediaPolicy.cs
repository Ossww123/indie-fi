namespace IndieFi.Web.Modules.Media.Domain;

public enum MediaTargetKind
{
    Game,
    Profile,
}

public enum MediaAttachmentKind
{
    GameCover,
    GameScreenshot,
    ProfileImage,
}

public sealed record MediaTarget
{
    private MediaTarget(MediaTargetKind kind, long id)
    {
        Kind = kind;
        Id = id;
    }

    public MediaTargetKind Kind { get; }
    public long Id { get; }

    public static MediaTarget Game(long gameId) =>
        Create(MediaTargetKind.Game, gameId, nameof(gameId));

    public static MediaTarget Profile(long userId) =>
        Create(MediaTargetKind.Profile, userId, nameof(userId));

    private static MediaTarget Create(
        MediaTargetKind kind,
        long id,
        string parameterName)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "A media target identifier must be positive.");
        }

        return new MediaTarget(kind, id);
    }
}

public sealed record MediaImageLimits(
    long MaximumOriginalBytes,
    int MaximumWidth,
    int MaximumHeight,
    long MaximumDecodedPixels);

public static class MediaPolicy
{
    public const long GameImageMaximumBytes = 10L * 1024 * 1024;
    public const long ProfileImageMaximumBytes = 5L * 1024 * 1024;
    public const long GameMaximumActiveOriginalBytes = 50L * 1024 * 1024;
    public const long ProfileMaximumActiveOriginalBytes = ProfileImageMaximumBytes;

    public const int GameCoverMaximumCount = 1;
    public const int GameScreenshotMaximumCount = 8;
    public const int ProfileImageMaximumCount = 1;
    public const int MaximumUploadPendingPerAccount = 10;
    public const int MaximumConcurrentReplacementCandidates = 1;

    public const int IssueLimitPerShortWindow = 20;
    public const int IssueLimitPerDay = 100;

    public static readonly TimeSpan IssueShortWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IssueDailyWindow = TimeSpan.FromDays(1);

    private static readonly MediaImageLimits GameImageLimits = new(
        GameImageMaximumBytes,
        MaximumWidth: 12_000,
        MaximumHeight: 12_000,
        MaximumDecodedPixels: 40_000_000);

    private static readonly MediaImageLimits ProfileImageLimits = new(
        ProfileImageMaximumBytes,
        MaximumWidth: 8_192,
        MaximumHeight: 8_192,
        MaximumDecodedPixels: 16_000_000);

    public static MediaTargetKind GetTargetKind(MediaAttachmentKind attachmentKind) =>
        attachmentKind switch
        {
            MediaAttachmentKind.GameCover => MediaTargetKind.Game,
            MediaAttachmentKind.GameScreenshot => MediaTargetKind.Game,
            MediaAttachmentKind.ProfileImage => MediaTargetKind.Profile,
            _ => throw new ArgumentOutOfRangeException(nameof(attachmentKind)),
        };

    public static MediaImageLimits GetImageLimits(MediaAttachmentKind attachmentKind) =>
        attachmentKind switch
        {
            MediaAttachmentKind.GameCover => GameImageLimits,
            MediaAttachmentKind.GameScreenshot => GameImageLimits,
            MediaAttachmentKind.ProfileImage => ProfileImageLimits,
            _ => throw new ArgumentOutOfRangeException(nameof(attachmentKind)),
        };

    public static int GetMaximumAttachedCount(MediaAttachmentKind attachmentKind) =>
        attachmentKind switch
        {
            MediaAttachmentKind.GameCover => GameCoverMaximumCount,
            MediaAttachmentKind.GameScreenshot => GameScreenshotMaximumCount,
            MediaAttachmentKind.ProfileImage => ProfileImageMaximumCount,
            _ => throw new ArgumentOutOfRangeException(nameof(attachmentKind)),
        };

    public static long GetMaximumActiveOriginalBytes(MediaTargetKind targetKind) =>
        targetKind switch
        {
            MediaTargetKind.Game => GameMaximumActiveOriginalBytes,
            MediaTargetKind.Profile => ProfileMaximumActiveOriginalBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
        };

    public static bool UsesSingleCurrentSlot(MediaAttachmentKind attachmentKind) =>
        attachmentKind switch
        {
            MediaAttachmentKind.GameCover => true,
            MediaAttachmentKind.GameScreenshot => false,
            MediaAttachmentKind.ProfileImage => true,
            _ => throw new ArgumentOutOfRangeException(nameof(attachmentKind)),
        };

    public static void RequireCompatible(
        MediaTarget target,
        MediaAttachmentKind attachmentKind)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != GetTargetKind(attachmentKind))
        {
            throw new ArgumentException(
                "The media attachment kind is not valid for the target.",
                nameof(attachmentKind));
        }
    }
}
