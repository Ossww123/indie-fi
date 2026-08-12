namespace IndieFi.Web.Entities;

public class FeedbackSubmission
{
    public long Id { get; set; }

    public long AssignmentId { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
