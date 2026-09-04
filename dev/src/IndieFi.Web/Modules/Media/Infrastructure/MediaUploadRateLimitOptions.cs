namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaUploadRateLimitOptions
{
    public const string SectionName = "Media:UploadRateLimit";

    public string KeyPrefix { get; set; } = "indiefi:media-upload:v1";

    public static bool IsValid(MediaUploadRateLimitOptions options) =>
        !string.IsNullOrWhiteSpace(options.KeyPrefix)
        && options.KeyPrefix.Length <= 100
        && options.KeyPrefix.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is ':' or '-' or '_');
}
