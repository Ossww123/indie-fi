using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace IndieFi.Web.Tests;

internal static class TestAccountProtection
{
    public static readonly byte[] EmailKey = Enumerable.Repeat((byte)21, 32).ToArray();
    public static readonly byte[] LookupKey = Enumerable.Repeat((byte)22, 32).ToArray();
    public static readonly byte[] MessageKey = Enumerable.Repeat((byte)23, 32).ToArray();
    public static readonly byte[] MessageKeyV2 = Enumerable.Repeat((byte)24, 32).ToArray();
    public static readonly byte[] RequestIpKey = Enumerable.Repeat((byte)25, 32).ToArray();
    public static readonly byte[] GameKeyEncryptionKey =
        Enumerable.Repeat((byte)26, 32).ToArray();
    public static readonly byte[] GameKeyFingerprintKey =
        Enumerable.Repeat((byte)27, 32).ToArray();

    public static EmailProtectionService CreateService()
    {
        return new EmailProtectionService(new Provider());
    }

    public static AuthenticationEmailMessageProtectionService CreateMessageProtectionService()
    {
        return new AuthenticationEmailMessageProtectionService(new Provider());
    }

    public static RequestIpProtectionService CreateRequestIpProtectionService()
    {
        return new RequestIpProtectionService(new Provider());
    }

    public static GameKeyProtectionService CreateGameKeyProtectionService()
    {
        return new GameKeyProtectionService(new Provider());
    }

    public static GameKey AddGameKey(
        AppDbContext db,
        KeyDrop drop,
        string value)
    {
        var parsed = GameKeyPolicy.ParseBatch(value);
        if (!parsed.IsValid || parsed.Keys.Count != 1)
        {
            throw new ArgumentException(
                "A valid test game key is required.",
                nameof(value));
        }

        var gameKeyValue = parsed.Keys[0];
        var protection = CreateGameKeyProtectionService();
        var protectedKey = protection.Protect(gameKeyValue);
        var gameKey = new GameKey
        {
            Drop = drop,
            Ciphertext = protectedKey.Ciphertext,
            Nonce = protectedKey.Nonce,
            AuthenticationTag = protectedKey.AuthenticationTag,
            EncryptionKeyId = protectedKey.EncryptionKeyId,
        };
        db.GameKeys.Add(gameKey);

        foreach (var fingerprint in
                 protection.CreateWritableFingerprints(gameKeyValue))
        {
            db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
            {
                GameKey = gameKey,
                KeyId = fingerprint.KeyId,
                Value = fingerprint.Value,
            });
        }

        return gameKey;
    }

    public static User AddUser(
        AppDbContext db,
        string email,
        string? passwordHash = "test-password-hash")
    {
        if (!AccountEmailAddressPolicy.TryCreate(email, out var address, out _))
        {
            throw new ArgumentException("A valid test email is required.", nameof(email));
        }

        var service = CreateService();
        var protectedEmail = service.Protect(address!);
        var user = new User
        {
            EmailCiphertext = protectedEmail.Ciphertext,
            EmailNonce = protectedEmail.Nonce,
            EmailAuthenticationTag = protectedEmail.AuthenticationTag,
            EmailEncryptionKeyId = protectedEmail.KeyId,
            PasswordHash = passwordHash,
        };

        db.Users.Add(user);
        foreach (var lookup in service.CreateWritableLookupValues(address!))
        {
            db.UserEmailLookups.Add(new UserEmailLookup
            {
                User = user,
                KeyId = lookup.KeyId,
                Value = lookup.Value,
            });
        }

        return user;
    }

    public static async Task<User> FindUserAsync(
        AppDbContext db,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (!AccountEmailAddressPolicy.TryCreate(email, out var address, out _))
        {
            throw new ArgumentException("A valid test email is required.", nameof(email));
        }

        var lookup = AssertSingleLookup(
            CreateService().CreateReadableLookupValues(address!));
        return await db.UserEmailLookups
            .Where(item =>
                item.KeyId == lookup.KeyId
                && item.Value.SequenceEqual(lookup.Value))
            .Select(item => item.User)
            .SingleAsync(cancellationToken);
    }

    private static EmailLookupValue AssertSingleLookup(
        IReadOnlyList<EmailLookupValue> lookups)
    {
        return lookups.Count == 1
            ? lookups[0]
            : throw new InvalidOperationException(
                "The test key provider must expose exactly one readable lookup key.");
    }

    public static Dictionary<string, string?> ConfigurationValues()
    {
        return new Dictionary<string, string?>
        {
            ["AccountProtection:Purposes:account-email-encryption:CurrentKeyId"] = "email-test-v1",
            ["AccountProtection:Purposes:account-email-encryption:ReadKeyIds:0"] = "email-test-v1",
            ["AccountProtection:Purposes:account-email-encryption:WriteKeyIds:0"] = "email-test-v1",
            ["AccountProtection:Purposes:account-email-encryption:Keys:email-test-v1"] = Convert.ToBase64String(EmailKey),
            ["AccountProtection:Purposes:account-email-lookup:CurrentKeyId"] = "lookup-test-v1",
            ["AccountProtection:Purposes:account-email-lookup:ReadKeyIds:0"] = "lookup-test-v1",
            ["AccountProtection:Purposes:account-email-lookup:WriteKeyIds:0"] = "lookup-test-v1",
            ["AccountProtection:Purposes:account-email-lookup:Keys:lookup-test-v1"] = Convert.ToBase64String(LookupKey),
            ["AccountProtection:Purposes:account-email-message-encryption:CurrentKeyId"] = "message-test-v1",
            ["AccountProtection:Purposes:account-email-message-encryption:ReadKeyIds:0"] = "message-test-v1",
            ["AccountProtection:Purposes:account-email-message-encryption:WriteKeyIds:0"] = "message-test-v1",
            ["AccountProtection:Purposes:account-email-message-encryption:Keys:message-test-v1"] = Convert.ToBase64String(MessageKey),
            ["AccountProtection:Purposes:account-request-ip-lookup:CurrentKeyId"] = "request-ip-test-v1",
            ["AccountProtection:Purposes:account-request-ip-lookup:ReadKeyIds:0"] = "request-ip-test-v1",
            ["AccountProtection:Purposes:account-request-ip-lookup:WriteKeyIds:0"] = "request-ip-test-v1",
            ["AccountProtection:Purposes:account-request-ip-lookup:Keys:request-ip-test-v1"] = Convert.ToBase64String(RequestIpKey),
            ["AccountProtection:Purposes:game-key-encryption:CurrentKeyId"] = "game-encryption-test-v1",
            ["AccountProtection:Purposes:game-key-encryption:ReadKeyIds:0"] = "game-encryption-test-v1",
            ["AccountProtection:Purposes:game-key-encryption:WriteKeyIds:0"] = "game-encryption-test-v1",
            ["AccountProtection:Purposes:game-key-encryption:Keys:game-encryption-test-v1"] = Convert.ToBase64String(GameKeyEncryptionKey),
            ["AccountProtection:Purposes:game-key-fingerprint:CurrentKeyId"] = "game-fingerprint-test-v1",
            ["AccountProtection:Purposes:game-key-fingerprint:ReadKeyIds:0"] = "game-fingerprint-test-v1",
            ["AccountProtection:Purposes:game-key-fingerprint:WriteKeyIds:0"] = "game-fingerprint-test-v1",
            ["AccountProtection:Purposes:game-key-fingerprint:Keys:game-fingerprint-test-v1"] = Convert.ToBase64String(GameKeyFingerprintKey),
            ["AuthenticationEmail:Enabled"] = "false",
        };
    }

    public static string CreateKeyFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"indiefi-account-protection-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, CreateKeyFileJson());
        return path;
    }

    public static string CreateKeyFileJson()
    {
        return JsonSerializer.Serialize(new
        {
            Purposes = new Dictionary<string, object>
            {
                [KeyPurpose.AccountEmailEncryption.Value] = Purpose(
                    "email-test-v1",
                    EmailKey),
                [KeyPurpose.AccountEmailLookup.Value] = Purpose(
                    "lookup-test-v1",
                    LookupKey),
                [KeyPurpose.AccountEmailMessageEncryption.Value] = Purpose(
                    "message-test-v1",
                    MessageKey),
                [KeyPurpose.AccountRequestIpLookup.Value] = Purpose(
                    "request-ip-test-v1",
                    RequestIpKey),
                [KeyPurpose.GameKeyEncryption.Value] = Purpose(
                    "game-encryption-test-v1",
                    GameKeyEncryptionKey),
                [KeyPurpose.GameKeyFingerprint.Value] = Purpose(
                    "game-fingerprint-test-v1",
                    GameKeyFingerprintKey),
            },
        });
    }

    private static object Purpose(string keyId, byte[] secret)
    {
        return new
        {
            CurrentKeyId = keyId,
            ReadKeyIds = new[] { keyId },
            WriteKeyIds = new[] { keyId },
            Keys = new Dictionary<string, string>
            {
                [keyId] = Convert.ToBase64String(secret),
            },
        };
    }

    private sealed class Provider : IKeyProvider
    {
        private static readonly KeyMaterial Email = new("email-test-v1", EmailKey);
        private static readonly KeyMaterial Lookup = new("lookup-test-v1", LookupKey);
        private static readonly KeyMaterial Message = new("message-test-v1", MessageKey);
        private static readonly KeyMaterial MessageV2 = new("message-test-v2", MessageKeyV2);
        private static readonly KeyMaterial RequestIp = new("request-ip-test-v1", RequestIpKey);
        private static readonly KeyMaterial GameEncryption =
            new("game-encryption-test-v1", GameKeyEncryptionKey);
        private static readonly KeyMaterial GameFingerprint =
            new("game-fingerprint-test-v1", GameKeyFingerprintKey);

        public KeyMaterial GetCurrent(KeyPurpose purpose) => Get(purpose);
        public KeyMaterial GetById(KeyPurpose purpose, string keyId)
        {
            if (purpose == KeyPurpose.AccountEmailMessageEncryption)
            {
                return keyId switch
                {
                    "message-test-v1" => Message,
                    "message-test-v2" => MessageV2,
                    _ => throw new InvalidOperationException($"Unknown test key: {keyId}"),
                };
            }

            var key = Get(purpose);
            return string.Equals(key.Id, keyId, StringComparison.Ordinal)
                ? key
                : throw new InvalidOperationException($"Unknown test key: {keyId}");
        }
        public IReadOnlyList<KeyMaterial> GetReadable(KeyPurpose purpose) => [Get(purpose)];
        public IReadOnlyList<KeyMaterial> GetWritable(KeyPurpose purpose) => [Get(purpose)];

        private static KeyMaterial Get(KeyPurpose purpose) => purpose switch
        {
            var value when value == KeyPurpose.AccountEmailEncryption => Email,
            var value when value == KeyPurpose.AccountEmailLookup => Lookup,
            var value when value == KeyPurpose.AccountEmailMessageEncryption => Message,
            var value when value == KeyPurpose.AccountRequestIpLookup => RequestIp,
            var value when value == KeyPurpose.GameKeyEncryption => GameEncryption,
            var value when value == KeyPurpose.GameKeyFingerprint => GameFingerprint,
            _ => throw new InvalidOperationException($"Unknown test purpose: {purpose}"),
        };
    }
}
