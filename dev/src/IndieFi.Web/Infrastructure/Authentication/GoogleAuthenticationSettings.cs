using Microsoft.Extensions.Options;

namespace IndieFi.Web.Infrastructure.Authentication;

public sealed class GoogleAuthenticationSettings
{
    public const string SectionName = "Authentication:Google";

    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsAvailable => Enabled && IsConfigured;

    public static ValidateOptionsResult Validate(GoogleAuthenticationSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var hasClientId = !string.IsNullOrWhiteSpace(options.ClientId);
        var hasClientSecret = !string.IsNullOrWhiteSpace(options.ClientSecret);

        if (!hasClientId && (options.Enabled || hasClientSecret))
        {
            failures.Add("Authentication:Google:ClientId is required.");
        }

        if (!hasClientSecret && (options.Enabled || hasClientId))
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
    public ValidateOptionsResult Validate(
        string? name,
        GoogleAuthenticationSettings options) =>
        GoogleAuthenticationSettings.Validate(options);
}
