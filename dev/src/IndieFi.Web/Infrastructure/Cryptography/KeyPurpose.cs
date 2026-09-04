namespace IndieFi.Web.Infrastructure.Cryptography;

public readonly record struct KeyPurpose(string Value)
{
    public static readonly KeyPurpose AccountEmailEncryption =
        new("account-email-encryption");

    public static readonly KeyPurpose AccountEmailLookup =
        new("account-email-lookup");

    public static readonly KeyPurpose AccountEmailMessageEncryption =
        new("account-email-message-encryption");

    public static readonly KeyPurpose AccountRequestIpLookup =
        new("account-request-ip-lookup");

    public static readonly KeyPurpose GameKeyEncryption =
        new("game-key-encryption");

    public static readonly KeyPurpose GameKeyFingerprint =
        new("game-key-fingerprint");

    public override string ToString() => Value;
}
