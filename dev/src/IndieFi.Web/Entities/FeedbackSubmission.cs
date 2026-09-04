using IndieFi.Web.Modules.Credits.Domain;

namespace IndieFi.Web.Entities;

public class FeedbackSubmission
{
    public long Id { get; set; }

    public long AssignmentId { get; set; }

    public long ReviewerId { get; set; }

    public long EarnEntryId { get; set; }

    public CreditLedgerEntry EarnEntry { get; set; } = null!;

    public string Body { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
