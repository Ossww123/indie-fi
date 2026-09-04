using System.Diagnostics;
using System.Diagnostics.Metrics;
using IndieFi.Web.Infrastructure.Observability;
using IndieFi.Web.Modules.KeyDistributions.Application;

namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public static class KeyDistributionTelemetry
{
    public const string MeterName = "IndieFi.KeyDistributions";

    private static readonly ActivitySource ActivitySource =
        new(ObservabilityActivitySources.KeyDistributions);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Claims = Meter.CreateCounter<long>(
        "indiefi.key_distributions.claims",
        "{claim}",
        "Key claim attempts by bounded outcome.");

    public static Activity? StartClaim() =>
        ActivitySource.StartActivity("key_claim", ActivityKind.Internal);

    public static void RecordClaim(ClaimOutcome outcome)
    {
        var outcomeName = outcome switch
        {
            ClaimOutcome.Granted => "granted",
            ClaimOutcome.AlreadyClaimed => "already_claimed",
            ClaimOutcome.NotOpen => "not_open",
            ClaimOutcome.SoldOut => "sold_out",
            ClaimOutcome.ClosedEarly => "closed_early",
            ClaimOutcome.Conflict => "conflict",
            ClaimOutcome.DropNotFound => "drop_not_found",
            ClaimOutcome.AccountUnavailable => "account_unavailable",
            ClaimOutcome.NotEligible => "not_eligible",
            ClaimOutcome.AdmissionRequired => "admission_required",
            ClaimOutcome.WaitingRoomTerminal => "waiting_room_terminal",
            ClaimOutcome.WaitingRoomUnavailable => "waiting_room_unavailable",
            _ => "other",
        };
        Claims.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcomeName));
        Activity.Current?.SetTag("indiefi.key_claim.outcome", outcomeName);
    }
}
