using Microsoft.Extensions.Configuration;

namespace IndieFi.Web.Infrastructure.Cryptography;

public sealed class FileKeyProvider : IKeyProvider
{
    private const string PurposesSection = "Purposes";

    private readonly ConfigurationKeyProvider _inner;

    public FileKeyProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        _inner = new ConfigurationKeyProvider(
            configuration.GetSection(PurposesSection));
    }

    public KeyMaterial GetCurrent(KeyPurpose purpose) =>
        _inner.GetCurrent(purpose);

    public KeyMaterial GetById(KeyPurpose purpose, string keyId) =>
        _inner.GetById(purpose, keyId);

    public IReadOnlyList<KeyMaterial> GetReadable(KeyPurpose purpose) =>
        _inner.GetReadable(purpose);

    public IReadOnlyList<KeyMaterial> GetWritable(KeyPurpose purpose) =>
        _inner.GetWritable(purpose);
}
