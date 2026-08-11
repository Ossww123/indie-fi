namespace IndieFi.Web.Entities;

public class ReviewAssignment
{
    public long Id { get; set; }

    public long RequestId { get; set; }

    public long GameId { get; set; }

    public long ReviewerId { get; set; }

    public ReviewAssignmentStatus Status { get; set; }

    public DateTimeOffset LeasedUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
