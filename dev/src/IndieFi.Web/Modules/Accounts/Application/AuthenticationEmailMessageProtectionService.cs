using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Domain;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed class AuthenticationEmailMessageProtectionService
{
    public const int NonceSize = 12;
    public const int AuthenticationTagSize = 16;

    private const int UserIdSize = sizeof(long);
    private const int KindSize = sizeof(int);
    private const int KeyIdLengthSize = sizeof(int);

    private static readonly byte[] AssociatedDataPrefix =
        "IndieFi:authentication-email-message:v1:"u8.ToArray();

    private readonly IKeyProvider _keyProvider;

    public AuthenticationEmailMessageProtectionService(IKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public ProtectedAuthenticationEmailPayload Protect(
        long userId,
        AuthenticationEmailMessageKind kind,
        string plaintext)
    {
        ValidateContext(userId, kind);
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var key = _keyProvider.GetCurrent(KeyPurpose.AccountEmailMessageEncryption);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[AuthenticationTagSize];
        var associatedData = BuildAssociatedData(userId, kind, key.Id);

        try
        {
            using var aes = new AesGcm(key.Secret.Span, AuthenticationTagSize);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        return new ProtectedAuthenticationEmailPayload(
            ciphertext,
            nonce,
            tag,
            key.Id);
    }

    public string Unprotect(
        long userId,
        AuthenticationEmailMessageKind kind,
        ProtectedAuthenticationEmailPayload protectedPayload)
    {
        ValidateContext(userId, kind);
        ArgumentNullException.ThrowIfNull(protectedPayload);

        if (protectedPayload.Ciphertext.Length == 0
            || protectedPayload.Nonce.Length != NonceSize
            || protectedPayload.AuthenticationTag.Length != AuthenticationTagSize)
        {
            throw new CryptographicException(
                "The protected authentication email payload has invalid metadata.");
        }

        var key = _keyProvider.GetById(
            KeyPurpose.AccountEmailMessageEncryption,
            protectedPayload.KeyId);
        var plaintext = new byte[protectedPayload.Ciphertext.Length];
        var associatedData = BuildAssociatedData(
            userId,
            kind,
            protectedPayload.KeyId);

        try
        {
            using var aes = new AesGcm(key.Secret.Span, AuthenticationTagSize);
            aes.Decrypt(
                protectedPayload.Nonce,
                protectedPayload.Ciphertext,
                protectedPayload.AuthenticationTag,
                plaintext,
                associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] BuildAssociatedData(
        long userId,
        AuthenticationEmailMessageKind kind,
        string keyId)
    {
        var encodedKeyId = Encoding.UTF8.GetBytes(keyId);
        var result = new byte[
            AssociatedDataPrefix.Length
            + UserIdSize
            + KindSize
            + KeyIdLengthSize
            + encodedKeyId.Length];
        var offset = 0;

        AssociatedDataPrefix.CopyTo(result, offset);
        offset += AssociatedDataPrefix.Length;
        BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(offset, UserIdSize), userId);
        offset += UserIdSize;
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, KindSize), (int)kind);
        offset += KindSize;
        BinaryPrimitives.WriteInt32BigEndian(
            result.AsSpan(offset, KeyIdLengthSize),
            encodedKeyId.Length);
        offset += KeyIdLengthSize;
        encodedKeyId.CopyTo(result, offset);

        return result;
    }

    private static void ValidateContext(
        long userId,
        AuthenticationEmailMessageKind kind)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
