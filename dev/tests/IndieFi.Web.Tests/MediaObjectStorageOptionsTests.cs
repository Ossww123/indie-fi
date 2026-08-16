using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MediaObjectStorageOptionsTests
{
    [Fact]
    public void Validate_WhenDisabled_AllowsEmptySettings()
    {
        var result = Validate(new MediaObjectStorageOptions(), Environments.Production);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InProduction_AcceptsCloudflareR2Settings()
    {
        var result = Validate(ValidOptions(), Environments.Production);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InDevelopment_AcceptsHttpPathStyleLocalService()
    {
        var options = ValidOptions();
        options.ServiceUrl = "http://127.0.0.1:9000";
        options.Region = "us-east-1";
        options.ForcePathStyle = true;

        var result = Validate(options, Environments.Development);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://storage.example.test")]
    [InlineData("https://user:secret@storage.example.test")]
    [InlineData("https://storage.example.test/path")]
    [InlineData("https://storage.example.test?token=secret")]
    [InlineData("https://storage.example.test/#fragment")]
    public void Validate_WithUnsafeServiceUrl_RejectsSettings(string serviceUrl)
    {
        var options = ValidOptions();
        options.ServiceUrl = serviceUrl;

        var result = Validate(options, Environments.Development);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("ServiceUrl"));
        Assert.DoesNotContain("secret", string.Join(' ', result.Failures!));
    }

    [Fact]
    public void Validate_InProduction_RejectsPlainHttpAndNonR2Region()
    {
        var options = ValidOptions();
        options.ServiceUrl = "http://storage.example.test";
        options.Region = "us-east-1";

        var result = Validate(options, Environments.Production);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("HTTPS"));
        Assert.Contains(result.Failures!, failure => failure.Contains("'auto'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPERCASE")]
    [InlineData("ab")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("ends-with-hyphen-")]
    [InlineData("contains.period")]
    [InlineData("contains space")]
    public void Validate_WithUnsafeBucketName_RejectsSettings(string bucketName)
    {
        var options = ValidOptions();
        options.BucketName = bucketName;

        var result = Validate(options, Environments.Production);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("BucketName"));
    }

    [Fact]
    public void Validate_WithMissingOrSharedCredentials_RejectsWithoutExposingValues()
    {
        const string sharedSecret = "must-never-appear-in-an-error";
        var options = ValidOptions();
        options.AccessKeyId = sharedSecret;
        options.SecretAccessKey = sharedSecret;

        var result = Validate(options, Environments.Production);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("must be different"));
        Assert.DoesNotContain(sharedSecret, string.Join(' ', result.Failures!));

        options.AccessKeyId = string.Empty;
        options.SecretAccessKey = string.Empty;
        result = Validate(options, Environments.Production);
        Assert.Contains(result.Failures!, failure => failure.Contains("AccessKeyId"));
        Assert.Contains(result.Failures!, failure => failure.Contains("SecretAccessKey"));
    }

    private static ValidateOptionsResult Validate(
        MediaObjectStorageOptions options,
        string environmentName)
    {
        var validator = new MediaObjectStorageOptionsValidator(
            new TestHostEnvironment(environmentName));
        return validator.Validate(Options.DefaultName, options);
    }

    private static MediaObjectStorageOptions ValidOptions() => new()
    {
        Enabled = true,
        ServiceUrl = "https://account-id.r2.cloudflarestorage.com",
        Region = "auto",
        BucketName = "indiefi-production",
        AccessKeyId = "r2-access-key",
        SecretAccessKey = "r2-secret-access-key",
    };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "IndieFi.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
