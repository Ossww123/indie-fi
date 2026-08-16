using IndieFi.Web.Modules.Media.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaObjectStorageHealthCheck : IHealthCheck
{
    private readonly S3MediaObjectStorage _storage;

    public MediaObjectStorageHealthCheck(S3MediaObjectStorage storage)
    {
        _storage = storage;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _storage.CheckReadyAsync(cancellationToken);
            return HealthCheckResult.Healthy(
                "Media object storage credentials and private bucket are reachable.");
        }
        catch (MediaObjectStorageException exception)
        {
            return HealthCheckResult.Unhealthy(
                "Media object storage is unavailable.",
                exception,
                new Dictionary<string, object>
                {
                    ["failure_code"] = exception.FailureCode.ToString(),
                });
        }
    }
}
