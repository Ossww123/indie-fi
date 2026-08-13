namespace IndieFi.Web.Modules.Accounts.Domain;

public sealed record ProtectedEmailAddress(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthenticationTag,
    string KeyId);

public sealed record EmailLookupValue(string KeyId, byte[] Value);
