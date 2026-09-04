using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Domain;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed class EmailProtectionService
{
    public const int NonceSize = 12;
    public const int AuthenticationTagSize = 16;
    public const int LookupValueSize = 32;

    private static readonly byte[] AssociatedDataPrefix =
        "IndieFi:account-email:v1:"u8.ToArray();

    private readonly IKeyProvider _keyProvider;

    public EmailProtectionService(IKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public ProtectedEmailAddress Protect(AccountEmailAddress email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var key = _keyProvider.GetCurrent(KeyPurpose.AccountEmailEncryption);
        var plaintext = Encoding.UTF8.GetBytes(email.DisplayValue);
        var ciphertext = new byte[plaintext.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[AuthenticationTagSize];
        var associatedData = BuildAssociatedData(key.Id);

        try
        {
            using var aes = new AesGcm(key.Secret.Span, AuthenticationTagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return new ProtectedEmailAddress(ciphertext, nonce, tag, key.Id);
    }

    public string Unprotect(ProtectedEmailAddress protectedEmail)
    {
        ArgumentNullException.ThrowIfNull(protectedEmail);

        if (protectedEmail.Nonce.Length != NonceSize
            || protectedEmail.AuthenticationTag.Length != AuthenticationTagSize)
        {
            throw new CryptographicException("The protected email has invalid metadata.");
        }

        var key = _keyProvider.GetById(
            KeyPurpose.AccountEmailEncryption,
            protectedEmail.KeyId);
        var plaintext = new byte[protectedEmail.Ciphertext.Length];
        var associatedData = BuildAssociatedData(protectedEmail.KeyId);

        try
        {
            using var aes = new AesGcm(key.Secret.Span, AuthenticationTagSize);
            aes.Decrypt(
                protectedEmail.Nonce,
                protectedEmail.Ciphertext,
                protectedEmail.AuthenticationTag,
                plaintext,
                associatedData);

            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public IReadOnlyList<EmailLookupValue> CreateWritableLookupValues(
        AccountEmailAddress email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return CreateLookupValues(
            email,
            _keyProvider.GetWritable(KeyPurpose.AccountEmailLookup));
    }

    public IReadOnlyList<EmailLookupValue> CreateReadableLookupValues(
        AccountEmailAddress email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return CreateLookupValues(
            email,
            _keyProvider.GetReadable(KeyPurpose.AccountEmailLookup));
    }

    private static IReadOnlyList<EmailLookupValue> CreateLookupValues(
        AccountEmailAddress email,
        IReadOnlyList<KeyMaterial> keys)
    {
        var normalizedEmail = Encoding.UTF8.GetBytes(email.NormalizedValue);

        try
        {
            return keys
                .Select(key => new EmailLookupValue(
                    key.Id,
                    HMACSHA256.HashData(key.Secret.Span, normalizedEmail)))
                .ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalizedEmail);
        }
    }

    private static byte[] BuildAssociatedData(string keyId)
    {
        var encodedKeyId = Encoding.UTF8.GetBytes(keyId);
        var result = new byte[AssociatedDataPrefix.Length + encodedKeyId.Length];
        AssociatedDataPrefix.CopyTo(result, 0);
        encodedKeyId.CopyTo(result, AssociatedDataPrefix.Length);
        return result;
    }
}
