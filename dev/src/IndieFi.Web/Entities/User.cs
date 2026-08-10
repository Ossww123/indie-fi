namespace IndieFi.Web.Entities;

/// <summary>
/// A registered account. One row per person who signs up.
/// </summary>
public class User
{
    public long Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// The credit ledger is the source of truth; this derived cache exists because
    /// a single column is the only form that supports a non-negative CHECK constraint.
    /// Always update it in the same transaction as its ledger entry, never on its own.
    /// </summary>
    public int CreditBalance { get; set; }

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
