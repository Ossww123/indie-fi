namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaWorkerOptions
{
    public const string SectionName = "Media:Worker";

    public bool Enabled { get; set; }

    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int RecoveryBatchSize { get; set; } = 50;

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int CleanupBatchSize { get; set; } = 50;

    public static bool IsValid(MediaWorkerOptions options) =>
        options.RecoveryInterval >= TimeSpan.FromSeconds(1)
        && options.RecoveryInterval <= TimeSpan.FromMinutes(5)
        && options.RecoveryBatchSize is >= 1 and <= 500
        && options.CleanupInterval >= TimeSpan.FromSeconds(10)
        && options.CleanupInterval <= TimeSpan.FromHours(1)
        && options.CleanupBatchSize is >= 1 and <= 500;
}
