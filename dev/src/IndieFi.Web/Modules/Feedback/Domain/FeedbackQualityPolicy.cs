namespace IndieFi.Web.Modules.Feedback.Domain;

public static class FeedbackQualityPolicy
{
    public const int MinimumScore = 1;
    public const int MaximumScore = 5;
    public const int MaximumReasonLength = 500;
    public const int InitialRevision = 1;
}
