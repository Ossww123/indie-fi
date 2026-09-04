using System.Security.Cryptography;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class EmailProtectionServiceTests
{
    [Fact]
    public void Protect_ThenUnprotect_RestoresDisplayEmail()
    {
        var service = CreateService();
        var email = CreateEmail("First.Last+game@Example.Test");

        var protectedEmail = service.Protect(email);

        Assert.Equal(email.DisplayValue, service.Unprotect(protectedEmail));
        Assert.Equal(EmailProtectionService.NonceSize, protectedEmail.Nonce.Length);
        Assert.Equal(
            EmailProtectionService.AuthenticationTagSize,
            protectedEmail.AuthenticationTag.Length);
        Assert.Equal("email-v1", protectedEmail.KeyId);
        Assert.DoesNotContain(
            email.DisplayValue,
            Convert.ToBase64String(protectedEmail.Ciphertext));
    }

    [Fact]
    public void ProtectingSameEmailTwice_UsesDifferentNonceAndCiphertext()
    {
        var service = CreateService();
        var email = CreateEmail("person@example.test");

        var first = service.Protect(email);
        var second = service.Protect(email);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Theory]
    [InlineData(ProtectedPart.Ciphertext)]
    [InlineData(ProtectedPart.Nonce)]
    [InlineData(ProtectedPart.AuthenticationTag)]
    public void Unprotect_WhenProtectedDataWasChanged_RejectsIt(ProtectedPart part)
    {
        var service = CreateService();
        var original = service.Protect(CreateEmail("person@example.test"));
        var changed = Copy(original);

        switch (part)
        {
            case ProtectedPart.Ciphertext:
                changed.Ciphertext[0] ^= 1;
                break;
            case ProtectedPart.Nonce:
                changed.Nonce[0] ^= 1;
                break;
            case ProtectedPart.AuthenticationTag:
                changed.AuthenticationTag[0] ^= 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(part));
        }

        Assert.ThrowsAny<CryptographicException>(() => service.Unprotect(changed));
    }

    [Fact]
    public void Unprotect_WhenKeyIdWasChanged_RejectsIt()
    {
        var service = CreateService();
        var original = service.Protect(CreateEmail("person@example.test"));
        var changed = original with { KeyId = "email-v2" };

        Assert.ThrowsAny<CryptographicException>(() => service.Unprotect(changed));
    }

    [Fact]
    public void CreateLookupValues_IsDeterministicPerKeyAndSeparatesVersions()
    {
        var service = CreateService();
        var firstForm = CreateEmail("Person@Example.Test");
        var secondForm = CreateEmail("person@example.test");

        var first = service.CreateWritableLookupValues(firstForm);
        var second = service.CreateWritableLookupValues(secondForm);

        Assert.Equal(2, first.Count);
        Assert.Equal(["lookup-v1", "lookup-v2"], first.Select(value => value.KeyId));
        Assert.Equal(first[0].Value, second[0].Value);
        Assert.Equal(first[1].Value, second[1].Value);
        Assert.NotEqual(first[0].Value, first[1].Value);
        Assert.All(
            first,
            value => Assert.Equal(
                EmailProtectionService.LookupValueSize,
                value.Value.Length));
    }

    private static EmailProtectionService CreateService()
    {
        var keys = new Dictionary<KeyPurpose, TestPurposeKeys>
        {
            [KeyPurpose.AccountEmailEncryption] = new(
                "email-v1",
                [
                    Key("email-v1", 1),
                    Key("email-v2", 2),
                ],
                ["email-v1", "email-v2"],
                ["email-v1"]),
            [KeyPurpose.AccountEmailLookup] = new(
                "lookup-v2",
                [
                    Key("lookup-v1", 3),
                    Key("lookup-v2", 4),
                ],
                ["lookup-v1", "lookup-v2"],
                ["lookup-v1", "lookup-v2"]),
        };

        return new EmailProtectionService(new TestKeyProvider(keys));
    }

    private static AccountEmailAddress CreateEmail(string value)
    {
        Assert.True(AccountEmailAddressPolicy.TryCreate(value, out var email, out _));
        return email!;
    }

    private static KeyMaterial Key(string id, byte fill)
    {
        return new KeyMaterial(id, Enumerable.Repeat(fill, 32).ToArray());
    }

    private static ProtectedEmailAddress Copy(ProtectedEmailAddress value)
    {
        return new ProtectedEmailAddress(
            value.Ciphertext.ToArray(),
            value.Nonce.ToArray(),
            value.AuthenticationTag.ToArray(),
            value.KeyId);
    }

    public enum ProtectedPart
    {
        Ciphertext,
        Nonce,
        AuthenticationTag,
    }

    private sealed record TestPurposeKeys(
        string CurrentKeyId,
        IReadOnlyList<KeyMaterial> Keys,
        IReadOnlyList<string> ReadKeyIds,
        IReadOnlyList<string> WriteKeyIds);

    private sealed class TestKeyProvider : IKeyProvider
    {
        private readonly IReadOnlyDictionary<KeyPurpose, TestPurposeKeys> _purposes;

        public TestKeyProvider(
            IReadOnlyDictionary<KeyPurpose, TestPurposeKeys> purposes)
        {
            _purposes = purposes;
        }

        public KeyMaterial GetCurrent(KeyPurpose purpose)
        {
            var keys = _purposes[purpose];
            return keys.Keys.Single(key => key.Id == keys.CurrentKeyId);
        }

        public KeyMaterial GetById(KeyPurpose purpose, string keyId)
        {
            var keys = _purposes[purpose];
            return keys.Keys.Single(key => key.Id == keyId);
        }

        public IReadOnlyList<KeyMaterial> GetReadable(KeyPurpose purpose)
        {
            var keys = _purposes[purpose];
            return keys.ReadKeyIds
                .Select(id => keys.Keys.Single(key => key.Id == id))
                .ToArray();
        }

        public IReadOnlyList<KeyMaterial> GetWritable(KeyPurpose purpose)
        {
            var keys = _purposes[purpose];
            return keys.WriteKeyIds
                .Select(id => keys.Keys.Single(key => key.Id == id))
                .ToArray();
        }
    }
}
