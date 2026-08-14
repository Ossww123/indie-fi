using IndieFi.Web.Infrastructure.Cryptography;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class ConfigurationKeyProviderTests
{
    [Fact]
    public void Constructor_WithSeparatedPurposeKeys_LoadsRotationSets()
    {
        var provider = new ConfigurationKeyProvider(
            BuildConfiguration(
                EmailKey: Bytes(1),
                LookupKey: Bytes(2),
                MessageKey: Bytes(3)));

        Assert.Equal(
            "email-v1",
            provider.GetCurrent(KeyPurpose.AccountEmailEncryption).Id);
        Assert.Equal(
            ["lookup-v1"],
            provider.GetReadable(KeyPurpose.AccountEmailLookup)
                .Select(key => key.Id));
        Assert.Equal(
            ["lookup-v1"],
            provider.GetWritable(KeyPurpose.AccountEmailLookup)
                .Select(key => key.Id));
        Assert.Equal(
            "message-v1",
            provider.GetCurrent(KeyPurpose.AccountEmailMessageEncryption).Id);
    }

    [Fact]
    public void Constructor_WhenPurposesShareSecret_RejectsConfiguration()
    {
        var shared = Bytes(1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ConfigurationKeyProvider(
                BuildConfiguration(
                    EmailKey: shared,
                    LookupKey: shared,
                    MessageKey: Bytes(3))));

        Assert.Contains("must not share", exception.Message);
        Assert.DoesNotContain(Convert.ToBase64String(shared), exception.Message);
    }

    [Fact]
    public void Constructor_WhenMessageAndStoredEmailShareSecret_RejectsConfiguration()
    {
        var shared = Bytes(1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ConfigurationKeyProvider(
                BuildConfiguration(
                    EmailKey: shared,
                    LookupKey: Bytes(2),
                    MessageKey: shared)));

        Assert.Contains("must not share", exception.Message);
        Assert.DoesNotContain(Convert.ToBase64String(shared), exception.Message);
    }

    [Fact]
    public void Constructor_WhenKeyHasWrongLength_RejectsWithoutPrintingSecret()
    {
        var invalid = Enumerable.Repeat((byte)7, 16).ToArray();

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ConfigurationKeyProvider(
                BuildConfiguration(
                    EmailKey: invalid,
                    LookupKey: Bytes(2),
                    MessageKey: Bytes(3))));

        Assert.Contains("must contain 32 bytes", exception.Message);
        Assert.DoesNotContain(Convert.ToBase64String(invalid), exception.Message);
    }

    private static IConfiguration BuildConfiguration(
        byte[] EmailKey,
        byte[] LookupKey,
        byte[] MessageKey)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AccountProtection:Purposes:account-email-encryption:CurrentKeyId"] = "email-v1",
                ["AccountProtection:Purposes:account-email-encryption:ReadKeyIds:0"] = "email-v1",
                ["AccountProtection:Purposes:account-email-encryption:WriteKeyIds:0"] = "email-v1",
                ["AccountProtection:Purposes:account-email-encryption:Keys:email-v1"] = Convert.ToBase64String(EmailKey),
                ["AccountProtection:Purposes:account-email-lookup:CurrentKeyId"] = "lookup-v1",
                ["AccountProtection:Purposes:account-email-lookup:ReadKeyIds:0"] = "lookup-v1",
                ["AccountProtection:Purposes:account-email-lookup:WriteKeyIds:0"] = "lookup-v1",
                ["AccountProtection:Purposes:account-email-lookup:Keys:lookup-v1"] = Convert.ToBase64String(LookupKey),
                ["AccountProtection:Purposes:account-email-message-encryption:CurrentKeyId"] = "message-v1",
                ["AccountProtection:Purposes:account-email-message-encryption:ReadKeyIds:0"] = "message-v1",
                ["AccountProtection:Purposes:account-email-message-encryption:WriteKeyIds:0"] = "message-v1",
                ["AccountProtection:Purposes:account-email-message-encryption:Keys:message-v1"] = Convert.ToBase64String(MessageKey),
            })
            .Build();
    }

    private static byte[] Bytes(byte fill)
    {
        return Enumerable.Repeat(fill, 32).ToArray();
    }
}
