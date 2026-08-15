namespace IndieFi.Web.Entities;

/// <summary>
/// An opaque game key assigned to a scheduled distribution. One row per key.
/// </summary>
public class GameKey
{
    public long Id { get; set; }

    public long DropId { get; set; }

    public byte[] Ciphertext { get; set; } = [];

    public byte[] Nonce { get; set; } = [];

    public byte[] AuthenticationTag { get; set; } = [];

    public string EncryptionKeyId { get; set; } = string.Empty;

    public KeyDrop Drop { get; set; } = null!;

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
