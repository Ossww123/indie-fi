using System.Diagnostics.Metrics;

namespace IndieFi.Web.Modules.Feedback.Infrastructure;

public enum ReviewerRecalculationOperation
{
    Single,
    Full,
}

public enum ReviewerRecalculationOutcome
{
    Completed,
    Failed,
    Cancelled,
}

public static class FeedbackTelemetry
{
    public const string MeterName = "IndieFi.Feedback";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Recalculations = Meter.CreateCounter<long>(
        "indiefi.feedback.reputation.recalculations",
        "{recalculation}",
        "Reviewer reputation recalculation attempts by operation and outcome.");
    private static readonly Counter<long> Reviewers = Meter.CreateCounter<long>(
        "indiefi.feedback.reputation.reviewers",
        "{reviewer}",
        "Reviewer reputation projections processed by bounded result.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "indiefi.feedback.reputation.duration",
        "ms",
        "Reviewer reputation recalculation duration.");

    public static void RecordReviewerRecalculation(
        ReviewerRecalculationOperation operation,
        ReviewerRecalculationOutcome outcome,
        int processedCount,
        int repairedCount,
        TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processedCount);
        if (repairedCount < 0 || repairedCount > processedCount)
        {
            throw new ArgumentOutOfRangeException(nameof(repairedCount));
        }

        var operationTag = new KeyValuePair<string, object?>(
            "operation",
            OperationName(operation));
        var outcomeTag = new KeyValuePair<string, object?>(
            "outcome",
            OutcomeName(outcome));
        Recalculations.Add(1, operationTag, outcomeTag);
        Duration.Record(duration.TotalMilliseconds, operationTag, outcomeTag);
        RecordReviewers(operationTag, "repaired", repairedCount);
        RecordReviewers(
            operationTag,
            "unchanged",
            processedCount - repairedCount);
    }

    private static void RecordReviewers(
        KeyValuePair<string, object?> operationTag,
        string result,
        int count)
    {
        if (count > 0)
        {
            Reviewers.Add(
                count,
                operationTag,
                new KeyValuePair<string, object?>("result", result));
        }
    }

    private static string OperationName(ReviewerRecalculationOperation operation) =>
        operation switch
        {
            ReviewerRecalculationOperation.Single => "single",
            ReviewerRecalculationOperation.Full => "full",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static string OutcomeName(ReviewerRecalculationOutcome outcome) =>
        outcome switch
        {
            ReviewerRecalculationOutcome.Completed => "completed",
            ReviewerRecalculationOutcome.Failed => "failed",
            ReviewerRecalculationOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
