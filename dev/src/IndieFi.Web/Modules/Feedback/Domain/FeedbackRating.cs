namespace IndieFi.Web.Modules.Feedback.Domain;

public sealed class FeedbackRating
{
    public long Id { get; set; }

    public long FeedbackSubmissionId { get; set; }

    public long RaterId { get; set; }

    public long ReviewerId { get; set; }

    public int Score { get; set; }

    public string? Reason { get; set; }

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
