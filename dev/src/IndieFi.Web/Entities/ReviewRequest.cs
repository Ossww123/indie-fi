namespace IndieFi.Web.Entities;

/// <summary>
/// A request for feedback on a game. Status is omitted because every request is pending until assignment exists.
/// </summary>
public class ReviewRequest
{
    public long Id { get; set; }

    public long GameId { get; set; }

    public long RequesterId { get; set; }

    public long SpendEntryId { get; set; }

    /// <summary>
    /// Lets EF fill SpendEntryId after inserting the ledger entry when both rows use one SaveChanges.
    /// </summary>
    public CreditLedgerEntry SpendEntry { get; set; } = null!;

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
