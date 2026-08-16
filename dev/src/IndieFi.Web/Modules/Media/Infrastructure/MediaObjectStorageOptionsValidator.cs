using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaObjectStorageOptionsValidator
    : IValidateOptions<MediaObjectStorageOptions>
{
    private const string Section = MediaObjectStorageOptions.SectionName;
    private readonly IHostEnvironment _environment;

    public MediaObjectStorageOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(
        string? name,
        MediaObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        ValidateServiceUrl(options.ServiceUrl, failures);

        if (!IsSimpleValue(options.Region, 64))
        {
            failures.Add($"{Section}:Region is required and must not contain whitespace or control characters.");
        }
        else if (_environment.IsProduction()
            && !string.Equals(options.Region, "auto", StringComparison.Ordinal))
        {
            failures.Add($"{Section}:Region must be 'auto' for Cloudflare R2 in production.");
        }

        if (!IsBucketName(options.BucketName))
        {
            failures.Add(
                $"{Section}:BucketName must contain 3 to 63 lowercase letters, numbers, or hyphens " +
                "and must start and end with a letter or number.");
        }

        if (!IsCredential(options.AccessKeyId, 256))
        {
            failures.Add($"{Section}:AccessKeyId is required and has an invalid format.");
        }

        if (!IsCredential(options.SecretAccessKey, 512))
        {
            failures.Add($"{Section}:SecretAccessKey is required and has an invalid format.");
        }

        if (IsCredential(options.AccessKeyId, 256)
            && IsCredential(options.SecretAccessKey, 512)
            && string.Equals(
                options.AccessKeyId,
                options.SecretAccessKey,
                StringComparison.Ordinal))
        {
            failures.Add($"{Section} access and secret keys must be different.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateServiceUrl(
        string value,
        ICollection<string> failures)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
        {
            failures.Add(
                $"{Section}:ServiceUrl must be an absolute HTTP(S) origin without credentials, " +
                "a path, query, or fragment.");
            return;
        }

        if (_environment.IsProduction() && uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{Section}:ServiceUrl must use HTTPS in production.");
        }
    }

    private static bool IsBucketName(string value)
    {
        return value.Length is >= 3 and <= 63
            && char.IsAsciiLetterOrDigit(value[0])
            && char.IsAsciiLetterOrDigit(value[^1])
            && value.All(character =>
                character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character == '-');
    }

    private static bool IsSimpleValue(string value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && !value.Any(char.IsWhiteSpace)
            && !value.Any(char.IsControl);
    }

    private static bool IsCredential(string value, int maximumLength)
    {
        return IsSimpleValue(value, maximumLength);
    }
}
