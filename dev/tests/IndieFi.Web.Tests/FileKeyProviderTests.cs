using IndieFi.Web.Infrastructure.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FileKeyProviderTests
{
    private readonly PostgreSqlFixture _fixture;

    public FileKeyProviderTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Constructor_WithDedicatedJson_LoadsSeparatedPurposeKeys()
    {
        var path = TestAccountProtection.CreateKeyFile();
        try
        {
            var provider = new FileKeyProvider(path);

            Assert.Equal(
                "email-test-v1",
                provider.GetCurrent(KeyPurpose.AccountEmailEncryption).Id);
            Assert.Equal(
                "lookup-test-v1",
                provider.GetCurrent(KeyPurpose.AccountEmailLookup).Id);
            Assert.Equal(
                "message-test-v1",
                provider.GetCurrent(KeyPurpose.AccountEmailMessageEncryption).Id);
            Assert.Equal(
                "request-ip-test-v1",
                provider.GetCurrent(KeyPurpose.AccountRequestIpLookup).Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_WhenKeyIdExceedsDatabaseLimit_RejectsKeyFile()
    {
        var tooLongKeyId = new string('k', 101);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"indiefi-invalid-key-id-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            TestAccountProtection.CreateKeyFileJson()
                .Replace("email-test-v1", tooLongKeyId, StringComparison.Ordinal));

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => new FileKeyProvider(path));

            Assert.Contains("100 characters or fewer", exception.Message);
            Assert.DoesNotContain(tooLongKeyId, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProductionStartup_WithoutDedicatedKeyFile_RejectsGeneralConfigurationKeys()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AccountProtectionKeyFile:Path"] = null,
                    }));
        });

        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        var validationException = FindException<OptionsValidationException>(exception);

        Assert.NotNull(validationException);
        Assert.Contains(
            validationException.Failures,
            failure => failure.Contains(
                "AccountProtectionKeyFile:Path",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionStartup_WithRelativeKeyFilePath_FailsValidation()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AccountProtectionKeyFile:Path"] = "account-protection.json",
                    }));
        });

        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        var validationException = FindException<OptionsValidationException>(exception);

        Assert.NotNull(validationException);
        Assert.Contains(
            validationException.Failures,
            failure => failure.Contains("must be absolute", StringComparison.Ordinal));
    }

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException expected)
            {
                return expected;
            }
        }

        return null;
    }
}
