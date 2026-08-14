namespace IndieFi.Web.Modules.Accounts.Domain;

public enum AuthenticationEmailMessageKind
{
    EmailConfirmation = 1,
}

public sealed record ProtectedAuthenticationEmailPayload(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] AuthenticationTag,
    string KeyId);

public sealed class AuthenticationEmailOutboxMessage
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public AuthenticationEmailMessageKind Kind { get; set; }
    public byte[] PayloadCiphertext { get; set; } = [];
    public byte[] PayloadNonce { get; set; } = [];
    public byte[] PayloadAuthenticationTag { get; set; } = [];
    public string PayloadKeyId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// The earliest time a delivery attempt may pick this message up. A failed attempt
    /// pushes it out so one undeliverable address cannot hold back the whole queue.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; set; }
}
