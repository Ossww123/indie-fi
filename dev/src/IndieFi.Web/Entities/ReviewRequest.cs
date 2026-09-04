using IndieFi.Web.Modules.Credits.Domain;

namespace IndieFi.Web.Entities;

/// <summary>
/// A request for feedback on a game.
/// </summary>
public class ReviewRequest
{
    public long Id { get; set; }

    public long GameId { get; set; }

    public long RequesterId { get; set; }

    public Guid CreationOperationId { get; set; }

    public long SpendEntryId { get; set; }

    /// <summary>
    /// Lets EF fill SpendEntryId after inserting the ledger entry when both rows use one SaveChanges.
    /// </summary>
    public CreditLedgerEntry SpendEntry { get; set; } = null!;

    public long? RefundEntryId { get; set; }

    public CreditLedgerEntry? RefundEntry { get; set; }

    public ReviewRequestStatus Status { get; set; } = ReviewRequestStatus.Queued;

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
