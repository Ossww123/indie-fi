namespace IndieFi.Web.Entities;

/// <summary>
/// A credit movement recorded for a user. One row per movement.
/// </summary>
public class CreditLedgerEntry
{
    public long Id { get; set; }

    public long UserId { get; set; }

    /// <summary>
    /// Lets EF populate the generated user ID when both rows are inserted by one SaveChanges.
    /// </summary>
    public User User { get; set; } = null!;

    public CreditEntryKind Kind { get; set; }

    public int Delta { get; set; }

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}

// Stored as text, so adding future values does not require a schema migration.
public enum CreditEntryKind
{
    Grant,
    Spend,
    Earn,
    Refund,
}
