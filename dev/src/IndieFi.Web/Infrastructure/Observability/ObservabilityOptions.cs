using System.Text.RegularExpressions;

namespace IndieFi.Web.Infrastructure.Observability;

public sealed partial class ObservabilityOptions
{
    public const string SectionName = "Observability";

    public bool Enabled { get; set; }

    public string OtlpEndpoint { get; set; } = "http://127.0.0.1:4317";

    public double TraceSamplingRatio { get; set; } = 0.1;

    public string ServiceVersion { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    public static bool IsValid(ObservabilityOptions options)
    {
        if (!options.Enabled)
        {
            return true;
        }

        return TryGetSafeEndpoint(options.OtlpEndpoint, out _)
            && options.TraceSamplingRatio is >= 0 and <= 1
            && IsOptionalResourceValue(options.ServiceVersion)
            && IsOptionalResourceValue(options.InstanceId);
    }

    public static bool TryGetSafeEndpoint(string value, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp
                && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private static bool IsOptionalResourceValue(string value) =>
        string.IsNullOrEmpty(value)
        || (value.Length <= 128 && ResourceValuePattern().IsMatch(value));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceValuePattern();
}
