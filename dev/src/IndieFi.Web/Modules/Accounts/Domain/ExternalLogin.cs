namespace IndieFi.Web.Modules.Accounts.Domain;

public sealed class ExternalLogin
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderUserId { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
}
