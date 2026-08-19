namespace IndieFi.Web.Modules.Feedback.Domain;

public sealed class ReviewerReputation
{
    public long ReviewerId { get; set; }

    public int CompletedReviewCount { get; set; }

    public int ExpiredAssignmentCount { get; set; }

    public int RatingCount { get; set; }

    public int RatingScoreSum { get; set; }

    public int Revision { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
