using IndieFi.Web.Entities;

namespace IndieFi.Web.Modules.Accounts.Domain;

public sealed class UserEmailLookup
{
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public string KeyId { get; set; } = string.Empty;
    public byte[] Value { get; set; } = [];
}
