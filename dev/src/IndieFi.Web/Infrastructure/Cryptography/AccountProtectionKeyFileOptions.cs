namespace IndieFi.Web.Infrastructure.Cryptography;

public sealed class AccountProtectionKeyFileOptions
{
    public const string SectionName = "AccountProtectionKeyFile";

    public string? Path { get; set; }
}
