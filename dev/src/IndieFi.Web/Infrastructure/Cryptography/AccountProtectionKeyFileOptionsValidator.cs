using Microsoft.Extensions.Options;

namespace IndieFi.Web.Infrastructure.Cryptography;

public sealed class AccountProtectionKeyFileOptionsValidator
    : IValidateOptions<AccountProtectionKeyFileOptions>
{
    private readonly IHostEnvironment _environment;

    public AccountProtectionKeyFileOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(
        string? name,
        AccountProtectionKeyFileOptions options)
    {
        if (!_environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            return ValidateOptionsResult.Fail(
                $"{AccountProtectionKeyFileOptions.SectionName}:Path is required in production.");
        }

        if (!System.IO.Path.IsPathFullyQualified(options.Path))
        {
            return ValidateOptionsResult.Fail(
                $"{AccountProtectionKeyFileOptions.SectionName}:Path must be absolute in production.");
        }

        try
        {
            _ = new FileKeyProvider(options.Path);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or FormatException
            or System.Text.Json.JsonException)
        {
            return ValidateOptionsResult.Fail(
                $"The production account protection key file is invalid or unreadable: {exception.Message}");
        }

        return ValidateOptionsResult.Success;
    }
}
