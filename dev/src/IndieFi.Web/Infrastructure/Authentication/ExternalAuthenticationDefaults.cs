namespace IndieFi.Web.Infrastructure.Authentication;

public static class ExternalAuthenticationDefaults
{
    public const string TemporaryScheme = "IndieFi.External";
    public const string TemporaryCookieName = ".IndieFi.External";
    public const string GoogleEmailVerifiedClaim = "indiefi:google_email_verified";

    public static readonly TimeSpan TemporaryLifetime = TimeSpan.FromMinutes(5);
}
