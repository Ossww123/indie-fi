namespace IndieFi.Web.Infrastructure.Cryptography;

public sealed class KeyMaterial
{
    public KeyMaterial(string id, ReadOnlySpan<byte> secret)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A key ID is required.", nameof(id));
        }

        Id = id;
        Secret = secret.ToArray();
    }

    public string Id { get; }

    public ReadOnlyMemory<byte> Secret { get; }
}
