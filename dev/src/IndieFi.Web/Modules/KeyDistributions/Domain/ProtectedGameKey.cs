namespace IndieFi.Web.Modules.KeyDistributions.Domain;

public sealed record ProtectedGameKey(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthenticationTag,
    string EncryptionKeyId)
{
    public override string ToString() => "[PROTECTED GAME KEY]";
}

public sealed record GameKeyFingerprint(
    string KeyId,
    byte[] Value)
{
    public override string ToString() => $"GameKeyFingerprint {{ KeyId = {KeyId} }}";
}
