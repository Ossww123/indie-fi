namespace IndieFi.Web.Infrastructure.Cryptography;

public interface IKeyProvider
{
    KeyMaterial GetCurrent(KeyPurpose purpose);

    KeyMaterial GetById(KeyPurpose purpose, string keyId);

    IReadOnlyList<KeyMaterial> GetReadable(KeyPurpose purpose);

    IReadOnlyList<KeyMaterial> GetWritable(KeyPurpose purpose);
}
