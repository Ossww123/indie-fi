using System.Text;

namespace IndieFi.Web.Modules.Feedback.Domain;

public static class FeedbackQualityPolicy
{
    public const int MinimumScore = 1;
    public const int MaximumScore = 5;
    public const int MaximumReasonLength = 500;
    public const int InitialRevision = 1;
    public const int RatingWindowDays = 30;

    public static bool IsValidScore(int score) =>
        score is >= MinimumScore and <= MaximumScore;

    public static bool TryNormalizeReason(
        string? input,
        out string? normalizedReason)
    {
        normalizedReason = input?.Trim();
        if (string.IsNullOrEmpty(normalizedReason))
        {
            normalizedReason = null;
            return true;
        }

        if (normalizedReason
            .EnumerateRunes()
            .Take(MaximumReasonLength + 1)
            .Count() > MaximumReasonLength)
        {
            normalizedReason = null;
            return false;
        }

        return true;
    }
}
