namespace IndieFi.Web.Modules.Accounts.Domain;

public sealed class PasswordResetToken
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
