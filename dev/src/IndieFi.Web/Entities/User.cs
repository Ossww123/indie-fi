namespace IndieFi.Web.Entities;

using IndieFi.Web.Modules.Accounts.Domain;

/// <summary>
/// A registered account. One row per person who signs up.
/// </summary>
public class User
{
    public long Id { get; set; }

    public byte[] EmailCiphertext { get; set; } = [];
    public byte[] EmailNonce { get; set; } = [];
    public byte[] EmailAuthenticationTag { get; set; } = [];
    public string EmailEncryptionKeyId { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public AccountStatus AccountStatus { get; set; } = AccountStatus.Active;
    public DateTimeOffset? SuspendedUntil { get; set; }
    public long SessionVersion { get; set; } = 1;

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
