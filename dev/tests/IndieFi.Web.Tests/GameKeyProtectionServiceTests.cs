using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class GameKeyProtectionServiceTests
{
    [Fact]
    public void Protect_ThenUnprotect_RoundTripsWithoutPlaintextMetadata()
    {
        const string secret = "ROUNDTRIP-GAME-KEY";
        var gameKey = ParseKey(secret);
        var service = TestAccountProtection.CreateGameKeyProtectionService();

        var protectedKey = service.Protect(gameKey);
        var unprotected = service.Unprotect(protectedKey);

        Assert.Equal(secret, unprotected.Value);
        Assert.Equal(GameKeyProtectionService.NonceSize, protectedKey.Nonce.Length);
        Assert.Equal(
            GameKeyProtectionService.AuthenticationTagSize,
            protectedKey.AuthenticationTag.Length);
        Assert.Equal("game-encryption-test-v1", protectedKey.EncryptionKeyId);
        Assert.False(
            Encoding.UTF8.GetBytes(secret).SequenceEqual(protectedKey.Ciphertext));
        Assert.DoesNotContain(secret, gameKey.ToString());
        Assert.DoesNotContain(secret, protectedKey.ToString());
    }

    [Fact]
    public void Protect_SameKeyTwice_UsesDifferentNonceAndCiphertext()
    {
        var service = TestAccountProtection.CreateGameKeyProtectionService();
        var gameKey = ParseKey("NONDETERMINISTIC-GAME-KEY");

        var first = service.Protect(gameKey);
        var second = service.Protect(gameKey);

        Assert.False(first.Nonce.SequenceEqual(second.Nonce));
        Assert.False(first.Ciphertext.SequenceEqual(second.Ciphertext));

        var firstFingerprint = service.CreateWritableFingerprints(gameKey).Single();
        var secondFingerprint = service.CreateWritableFingerprints(gameKey).Single();
        Assert.True(firstFingerprint.Value.SequenceEqual(secondFingerprint.Value));
    }

    [Fact]
    public void Unprotect_WhenCiphertextIsTampered_RejectsWithoutExposingSecret()
    {
        const string secret = "TAMPERED-GAME-KEY";
        var service = TestAccountProtection.CreateGameKeyProtectionService();
        var protectedKey = service.Protect(ParseKey(secret));
        var tamperedCiphertext = protectedKey.Ciphertext.ToArray();
        tamperedCiphertext[0] ^= 1;

        var exception = Assert.ThrowsAny<CryptographicException>(() =>
            service.Unprotect(protectedKey with
            {
                Ciphertext = tamperedCiphertext,
            }));

        Assert.DoesNotContain(secret, exception.ToString());
    }

    [Theory]
    [InlineData(0, GameKeyProtectionService.AuthenticationTagSize)]
    [InlineData(GameKeyProtectionService.NonceSize, 0)]
    public void Unprotect_WithInvalidMetadata_RejectsBeforeDecryption(
        int nonceSize,
        int authenticationTagSize)
    {
        var service = TestAccountProtection.CreateGameKeyProtectionService();
        var protectedKey = service.Protect(ParseKey("METADATA-GAME-KEY"));

        var exception = Assert.Throws<CryptographicException>(() =>
            service.Unprotect(protectedKey with
            {
                Nonce = new byte[nonceSize],
                AuthenticationTag = new byte[authenticationTagSize],
            }));

        Assert.Contains("invalid metadata", exception.Message);
    }

    [Fact]
    public void Fingerprints_AreStableVersionedAndDoNotExposeValueInText()
    {
        var service = new GameKeyProtectionService(
            new RotatingGameKeyProvider(useNewEncryptionKey: true));
        var gameKey = ParseKey("FINGERPRINT-GAME-KEY");

        var first = service.CreateWritableFingerprints(gameKey);
        var repeated = service.CreateWritableFingerprints(gameKey);

        Assert.Equal(["game-fingerprint-v1", "game-fingerprint-v2"],
            first.Select(fingerprint => fingerprint.KeyId));
        Assert.All(first, fingerprint =>
            Assert.Equal(
                GameKeyProtectionService.FingerprintSize,
                fingerprint.Value.Length));
        Assert.True(first[0].Value.SequenceEqual(repeated[0].Value));
        Assert.True(first[1].Value.SequenceEqual(repeated[1].Value));
        Assert.False(first[0].Value.SequenceEqual(first[1].Value));
        Assert.DoesNotContain(gameKey.Value, first[0].ToString());
    }

    [Fact]
    public void Fingerprints_DifferentOpaqueValuesAreDifferent()
    {
        var service = TestAccountProtection.CreateGameKeyProtectionService();

        var upper = service.CreateWritableFingerprints(ParseKey("ABC-123")).Single();
        var lower = service.CreateWritableFingerprints(ParseKey("abc-123")).Single();

        Assert.False(upper.Value.SequenceEqual(lower.Value));
    }

    [Fact]
    public void Unprotect_DuringRotation_ReadsCiphertextFromPreviousKey()
    {
        var oldService = new GameKeyProtectionService(
            new RotatingGameKeyProvider(useNewEncryptionKey: false));
        var rotatingService = new GameKeyProtectionService(
            new RotatingGameKeyProvider(useNewEncryptionKey: true));
        var gameKey = ParseKey("ROTATION-GAME-KEY");
        var protectedWithOldKey = oldService.Protect(gameKey);

        var result = rotatingService.Unprotect(protectedWithOldKey);

        Assert.Equal("game-encryption-v1", protectedWithOldKey.EncryptionKeyId);
        Assert.Equal(gameKey.Value, result.Value);
    }

    private static GameKeyValue ParseKey(string value)
    {
        var result = GameKeyPolicy.ParseBatch(value);
        Assert.True(result.IsValid);
        return Assert.Single(result.Keys);
    }

    private sealed class RotatingGameKeyProvider : IKeyProvider
    {
        private static readonly KeyMaterial EncryptionV1 =
            new("game-encryption-v1", Bytes(31));
        private static readonly KeyMaterial EncryptionV2 =
            new("game-encryption-v2", Bytes(32));
        private static readonly KeyMaterial FingerprintV1 =
            new("game-fingerprint-v1", Bytes(33));
        private static readonly KeyMaterial FingerprintV2 =
            new("game-fingerprint-v2", Bytes(34));

        private readonly bool _useNewEncryptionKey;

        public RotatingGameKeyProvider(bool useNewEncryptionKey)
        {
            _useNewEncryptionKey = useNewEncryptionKey;
        }

        public KeyMaterial GetCurrent(KeyPurpose purpose)
        {
            if (purpose == KeyPurpose.GameKeyEncryption)
            {
                return _useNewEncryptionKey ? EncryptionV2 : EncryptionV1;
            }

            if (purpose == KeyPurpose.GameKeyFingerprint)
            {
                return FingerprintV2;
            }

            throw new InvalidOperationException($"Unexpected purpose: {purpose}");
        }

        public KeyMaterial GetById(KeyPurpose purpose, string keyId)
        {
            if (purpose != KeyPurpose.GameKeyEncryption)
            {
                throw new InvalidOperationException($"Unexpected purpose: {purpose}");
            }

            return keyId switch
            {
                "game-encryption-v1" => EncryptionV1,
                "game-encryption-v2" => EncryptionV2,
                _ => throw new InvalidOperationException($"Unexpected key ID: {keyId}"),
            };
        }

        public IReadOnlyList<KeyMaterial> GetReadable(KeyPurpose purpose)
        {
            if (purpose == KeyPurpose.GameKeyEncryption)
            {
                return [EncryptionV1, EncryptionV2];
            }

            if (purpose == KeyPurpose.GameKeyFingerprint)
            {
                return [FingerprintV1, FingerprintV2];
            }

            throw new InvalidOperationException($"Unexpected purpose: {purpose}");
        }

        public IReadOnlyList<KeyMaterial> GetWritable(KeyPurpose purpose)
        {
            if (purpose == KeyPurpose.GameKeyEncryption)
            {
                return [_useNewEncryptionKey ? EncryptionV2 : EncryptionV1];
            }

            if (purpose == KeyPurpose.GameKeyFingerprint)
            {
                return [FingerprintV1, FingerprintV2];
            }

            throw new InvalidOperationException($"Unexpected purpose: {purpose}");
        }

        private static byte[] Bytes(byte value) =>
            Enumerable.Repeat(value, 32).ToArray();
    }
}
