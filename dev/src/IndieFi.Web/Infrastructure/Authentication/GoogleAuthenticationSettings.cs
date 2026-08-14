using Microsoft.Extensions.Options;

namespace IndieFi.Web.Infrastructure.Authentication;

public sealed class GoogleAuthenticationSettings
{
    public const string SectionName = "Authentication:Google";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    public static ValidateOptionsResult Validate(
        GoogleAuthenticationSettings options,
        bool requireCredentials)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var hasClientId = !string.IsNullOrWhiteSpace(options.ClientId);
        var hasClientSecret = !string.IsNullOrWhiteSpace(options.ClientSecret);

        if (!hasClientId && (requireCredentials || hasClientSecret))
        {
            failures.Add("Authentication:Google:ClientId is required.");
        }

        if (!hasClientSecret && (requireCredentials || hasClientId))
        {
            failures.Add("Authentication:Google:ClientSecret is required.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class GoogleAuthenticationSettingsValidator
    : IValidateOptions<GoogleAuthenticationSettings>
{
    private readonly IHostEnvironment _environment;

    public GoogleAuthenticationSettingsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(
        string? name,
        GoogleAuthenticationSettings options) =>
        GoogleAuthenticationSettings.Validate(
            options,
            requireCredentials: !_environment.IsDevelopment());
}
