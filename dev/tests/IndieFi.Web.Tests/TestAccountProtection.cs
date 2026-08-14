using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;

namespace IndieFi.Web.Tests;

internal static class TestAccountProtection
{
    public static readonly byte[] EmailKey = Enumerable.Repeat((byte)21, 32).ToArray();
    public static readonly byte[] LookupKey = Enumerable.Repeat((byte)22, 32).ToArray();

    public static EmailProtectionService CreateService()
    {
        return new EmailProtectionService(new Provider());
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
