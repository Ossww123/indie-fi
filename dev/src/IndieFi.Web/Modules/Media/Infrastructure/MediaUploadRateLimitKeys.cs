using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class MediaUploadRateLimitKeys
{
    private readonly string _prefix;

    public MediaUploadRateLimitKeys(IOptions<MediaUploadRateLimitOptions> options)
    {
        _prefix = options.Value.KeyPrefix;
    }

    public RedisKey Issues(long ownerUserId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        return $"{_prefix}:{{account:{ownerUserId}}}:issues";
    }
}
