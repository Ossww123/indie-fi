namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public sealed class WaitingRoomPolicyOptions
{
    public const string SectionName = "WaitingRoom:Policy";

    public TimeSpan OpensBefore { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan AdmissionLifetime { get; set; } = TimeSpan.FromMinutes(2);

    public int ConcurrentAdmissionLimit { get; set; } = 10;

    public TimeSpan ActiveStateTtl { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan TerminalStateTtl { get; set; } = TimeSpan.FromHours(1);

    public static bool IsValid(WaitingRoomPolicyOptions options) =>
        IsBetween(options.OpensBefore, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1))
        && IsBetween(options.ReconnectGrace, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1))
        && IsBetween(options.AdmissionLifetime, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1))
        && options.ConcurrentAdmissionLimit is >= 1 and <= 1000
        && IsBetween(options.ActiveStateTtl, TimeSpan.FromHours(1), TimeSpan.FromDays(30))
        && IsBetween(options.TerminalStateTtl, TimeSpan.FromMinutes(1), TimeSpan.FromDays(7));

    private static bool IsBetween(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value >= minimum && value <= maximum;
}
