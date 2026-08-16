namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaObjectStorageOptions
{
    public const string SectionName = "Media:ObjectStorage";

    public bool Enabled { get; set; }

    public string ServiceUrl { get; set; } = string.Empty;

    public string Region { get; set; } = "auto";

    public string BucketName { get; set; } = string.Empty;

    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Local S3-compatible services commonly require path-style bucket addressing.
    /// Cloudflare R2 uses virtual-host style in production.
    /// </summary>
    public bool ForcePathStyle { get; set; }
}
