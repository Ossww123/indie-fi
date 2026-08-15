namespace IndieFi.Web.Entities;

/// <summary>
/// A versioned, keyed fingerprint used to detect duplicate game keys globally.
/// </summary>
public sealed class StoredGameKeyFingerprint
{
    public long GameKeyId { get; set; }

    public string KeyId { get; set; } = string.Empty;

    public byte[] Value { get; set; } = [];

    public GameKey GameKey { get; set; } = null!;
}
