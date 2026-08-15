using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.KeyDistributions.Domain;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class GameKeyProtectionService
{
    public const int NonceSize = 12;
    public const int AuthenticationTagSize = 16;
    public const int FingerprintSize = 32;

    private static readonly byte[] EncryptionAssociatedDataPrefix =
        "IndieFi:game-key-encryption:v1:"u8.ToArray();

    private static readonly byte[] FingerprintPurposePrefix =
        "IndieFi:game-key-fingerprint:v1:"u8.ToArray();

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IKeyProvider _keyProvider;

    public GameKeyProtectionService(IKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public ProtectedGameKey Protect(GameKeyValue gameKey)
    {
        ArgumentNullException.ThrowIfNull(gameKey);

        var key = _keyProvider.GetCurrent(KeyPurpose.GameKeyEncryption);
        var plaintext = StrictUtf8.GetBytes(gameKey.Value);
        var ciphertext = new byte[plaintext.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var authenticationTag = new byte[AuthenticationTagSize];
        var associatedData = BuildEncryptionAssociatedData(key.Id);

        try
        {
            using var aes = new AesGcm(key.Secret.Span, AuthenticationTagSize);
            aes.Encrypt(
                nonce,
                plaintext,
                ciphertext,
                authenticationTag,
                associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return new ProtectedGameKey(
            ciphertext,
            nonce,
            authenticationTag,
            key.Id);
    }

    public GameKeyValue Unprotect(ProtectedGameKey protectedGameKey)
    {
        ArgumentNullException.ThrowIfNull(protectedGameKey);
        ValidateMetadata(protectedGameKey);

        var key = _keyProvider.GetById(
            KeyPurpose.GameKeyEncryption,
            protectedGameKey.EncryptionKeyId);
        var plaintext = new byte[protectedGameKey.Ciphertext.Length];
        var associatedData = BuildEncryptionAssociatedData(
            protectedGameKey.EncryptionKeyId);

        try
        {
            using var aes = new AesGcm(key.Secret.Span, AuthenticationTagSize);
            aes.Decrypt(
                protectedGameKey.Nonce,
                protectedGameKey.Ciphertext,
                protectedGameKey.AuthenticationTag,
                plaintext,
                associatedData);

            string value;
            try
            {
                value = StrictUtf8.GetString(plaintext);
            }
            catch (DecoderFallbackException exception)
            {
                throw new CryptographicException(
                    "The protected game key plaintext is not valid UTF-8.",
                    exception);
            }

            try
            {
                return new GameKeyValue(value);
            }
            catch (ArgumentException exception)
            {
                throw new CryptographicException(
                    "The protected game key plaintext violates the game key policy.",
                    exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public IReadOnlyList<GameKeyFingerprint> CreateWritableFingerprints(
        GameKeyValue gameKey)
    {
        ArgumentNullException.ThrowIfNull(gameKey);
        return CreateFingerprints(
            gameKey,
            _keyProvider.GetWritable(KeyPurpose.GameKeyFingerprint));
    }

    public IReadOnlyList<GameKeyFingerprint> CreateReadableFingerprints(
        GameKeyValue gameKey)
    {
        ArgumentNullException.ThrowIfNull(gameKey);
        return CreateFingerprints(
            gameKey,
            _keyProvider.GetReadable(KeyPurpose.GameKeyFingerprint));
    }

    private static IReadOnlyList<GameKeyFingerprint> CreateFingerprints(
        GameKeyValue gameKey,
        IReadOnlyList<KeyMaterial> keys)
    {
        var encodedValue = StrictUtf8.GetBytes(gameKey.Value);
        var payload = new byte[FingerprintPurposePrefix.Length + encodedValue.Length];
        FingerprintPurposePrefix.CopyTo(payload, 0);
        encodedValue.CopyTo(payload, FingerprintPurposePrefix.Length);

        try
        {
            return keys
                .Select(key => new GameKeyFingerprint(
                    key.Id,
                    HMACSHA256.HashData(key.Secret.Span, payload)))
                .ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedValue);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void ValidateMetadata(ProtectedGameKey protectedGameKey)
    {
        var maximumCiphertextSize = GameKeyPolicy.MaxLength * 4;
        if (protectedGameKey.Ciphertext is null
            || protectedGameKey.Ciphertext.Length is 0
            || protectedGameKey.Ciphertext.Length > maximumCiphertextSize
            || protectedGameKey.Nonce is null
            || protectedGameKey.Nonce.Length != NonceSize
            || protectedGameKey.AuthenticationTag is null
            || protectedGameKey.AuthenticationTag.Length != AuthenticationTagSize
            || string.IsNullOrWhiteSpace(protectedGameKey.EncryptionKeyId))
        {
            throw new CryptographicException(
                "The protected game key has invalid metadata.");
        }
    }

    private static byte[] BuildEncryptionAssociatedData(string keyId)
    {
        var encodedKeyId = StrictUtf8.GetBytes(keyId);
        var result = new byte[
            EncryptionAssociatedDataPrefix.Length + encodedKeyId.Length];
        EncryptionAssociatedDataPrefix.CopyTo(result, 0);
        encodedKeyId.CopyTo(result, EncryptionAssociatedDataPrefix.Length);
        return result;
    }
}
