using System.Diagnostics;

namespace IndieFi.Web.Infrastructure.Observability;

public static class ObservabilityPrivacy
{
    public static bool ShouldTraceInboundHttp(HttpContext context) =>
        !context.Request.Path.StartsWithSegments(
            "/health",
            StringComparison.OrdinalIgnoreCase);

    public static void SanitizeInboundHttp(
        Activity activity,
        HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(request);

        RemovePotentiallySensitiveUrlTags(activity);
    }

    public static void SanitizeOutboundHttp(
        Activity activity,
        HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(request);

        RemovePotentiallySensitiveUrlTags(activity);
    }

    private static void RemovePotentiallySensitiveUrlTags(Activity activity)
    {
        activity.SetTag("url.full", null);
        activity.SetTag("url.path", null);
        activity.SetTag("url.query", null);
        activity.SetTag("http.url", null);
        activity.SetTag("http.target", null);
    }
}
