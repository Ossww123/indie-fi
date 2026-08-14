namespace IndieFi.Web.Modules.Accounts.Domain;

public sealed class EmailConfirmationResendAttempt
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public byte[] RequestIpHash { get; set; } = [];
    public string RequestIpHashKeyId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
