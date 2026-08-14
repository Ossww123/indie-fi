namespace IndieFi.Web.Modules.Accounts.Domain;

public static class ExternalLoginPolicy
{
    public const string GoogleProvider = "Google";
    public const int ProviderMaxLength = 32;
    public const int ProviderUserIdMaxLength = 255;

    public static bool IsSupportedProvider(string? provider) =>
        string.Equals(provider, GoogleProvider, StringComparison.Ordinal);

    public static bool IsValidProviderUserId(string? providerUserId) =>
        !string.IsNullOrWhiteSpace(providerUserId)
        && providerUserId.Length <= ProviderUserIdMaxLength;
}
