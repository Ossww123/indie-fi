namespace IndieFi.Web.Infrastructure.Cryptography;

public readonly record struct KeyPurpose(string Value)
{
    public static readonly KeyPurpose AccountEmailEncryption =
        new("account-email-encryption");

    public static readonly KeyPurpose AccountEmailLookup =
        new("account-email-lookup");

    public override string ToString() => Value;
}
