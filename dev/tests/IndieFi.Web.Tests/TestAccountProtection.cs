using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Tests;

internal static class TestAccountProtection
{
    public static readonly byte[] EmailKey = Enumerable.Repeat((byte)21, 32).ToArray();
    public static readonly byte[] LookupKey = Enumerable.Repeat((byte)22, 32).ToArray();

    public static EmailProtectionService CreateService()
    {
        return new EmailProtectionService(new Provider());
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
        };
    }

    private sealed class Provider : IKeyProvider
    {
        private static readonly KeyMaterial Email = new("email-test-v1", EmailKey);
        private static readonly KeyMaterial Lookup = new("lookup-test-v1", LookupKey);

        public KeyMaterial GetCurrent(KeyPurpose purpose) => Get(purpose);
        public KeyMaterial GetById(KeyPurpose purpose, string keyId)
        {
            var key = Get(purpose);
            return string.Equals(key.Id, keyId, StringComparison.Ordinal)
                ? key
                : throw new InvalidOperationException($"Unknown test key: {keyId}");
        }
        public IReadOnlyList<KeyMaterial> GetReadable(KeyPurpose purpose) => [Get(purpose)];
        public IReadOnlyList<KeyMaterial> GetWritable(KeyPurpose purpose) => [Get(purpose)];

        private static KeyMaterial Get(KeyPurpose purpose) =>
            purpose == KeyPurpose.AccountEmailEncryption ? Email : Lookup;
    }
}
