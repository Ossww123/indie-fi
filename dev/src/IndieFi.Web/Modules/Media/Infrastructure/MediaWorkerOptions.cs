namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaWorkerOptions
{
    public const string SectionName = "Media:Worker";

    public bool Enabled { get; set; }

    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int RecoveryBatchSize { get; set; } = 50;

    public static bool IsValid(MediaWorkerOptions options) =>
        options.RecoveryInterval >= TimeSpan.FromSeconds(1)
        && options.RecoveryInterval <= TimeSpan.FromMinutes(5)
        && options.RecoveryBatchSize is >= 1 and <= 500;
}
