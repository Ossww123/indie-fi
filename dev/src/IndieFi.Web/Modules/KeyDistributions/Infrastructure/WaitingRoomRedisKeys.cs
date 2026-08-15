using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public sealed class WaitingRoomRedisKeys
{
    private readonly string _prefix;

    public WaitingRoomRedisKeys(IOptions<WaitingRoomRedisOptions> options)
    {
        _prefix = options.Value.KeyPrefix;
    }

    public RedisKey Metadata(long dropId) => Build(dropId, "meta");

    public RedisKey Entries(long dropId) => Build(dropId, "entries");

    public RedisKey PreWaiting(long dropId) => Build(dropId, "pre");

    public RedisKey Waiting(long dropId) => Build(dropId, "queue");

    public RedisKey Admitted(long dropId) => Build(dropId, "admitted");

    public RedisKey[] All(long dropId) =>
    [
        Metadata(dropId),
        Entries(dropId),
        PreWaiting(dropId),
        Waiting(dropId),
        Admitted(dropId),
    ];

    private RedisKey Build(long dropId, string suffix)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        return $"{_prefix}:{{drop:{dropId}}}:{suffix}";
    }
}
