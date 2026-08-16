using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using StackExchange.Redis;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class RedisMediaUploadIssueRateLimiter : IMediaUploadIssueRateLimiter
{
    private const string AcquireScript = """
        local current = redis.call('TIME')
        local now = (tonumber(current[1]) * 1000) + math.floor(tonumber(current[2]) / 1000)
        local short_window = tonumber(ARGV[2])
        local daily_window = tonumber(ARGV[3])

        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - daily_window)

        if redis.call('ZSCORE', KEYS[1], ARGV[1]) then
            return 1
        end

        local daily_count = redis.call('ZCARD', KEYS[1])
        if daily_count >= tonumber(ARGV[5]) then
            return 3
        end

        local short_count = redis.call(
            'ZCOUNT',
            KEYS[1],
            '(' .. tostring(now - short_window),
            '+inf')
        if short_count >= tonumber(ARGV[4]) then
            return 2
        end

        redis.call('ZADD', KEYS[1], now, ARGV[1])
        redis.call('PEXPIRE', KEYS[1], daily_window)
        return 1
        """;

    private readonly WaitingRoomRedisConnection _connection;
    private readonly MediaUploadRateLimitKeys _keys;

    public RedisMediaUploadIssueRateLimiter(
        WaitingRoomRedisConnection connection,
        MediaUploadRateLimitKeys keys)
    {
        _connection = connection;
        _keys = keys;
    }

    public async Task<MediaUploadIssueRateLimitOutcome> TryAcquireAsync(
        long ownerUserId,
        Guid issueId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        if (issueId == Guid.Empty)
        {
            throw new ArgumentException(
                "An upload issue identifier must not be empty.",
                nameof(issueId));
        }

        try
        {
            var connection = await _connection.GetAsync(cancellationToken);
            var result = await connection.GetDatabase().ScriptEvaluateAsync(
                AcquireScript,
                [_keys.Issues(ownerUserId)],
                [
                    issueId.ToString("N"),
                    ToMilliseconds(MediaPolicy.IssueShortWindow),
                    ToMilliseconds(MediaPolicy.IssueDailyWindow),
                    MediaPolicy.IssueLimitPerShortWindow,
                    MediaPolicy.IssueLimitPerDay,
                ],
                CommandFlags.DemandMaster);

            return (int)result switch
            {
                1 => MediaUploadIssueRateLimitOutcome.Allowed,
                2 => MediaUploadIssueRateLimitOutcome.ShortWindowLimitReached,
                3 => MediaUploadIssueRateLimitOutcome.DailyLimitReached,
                var unknown => throw new InvalidOperationException(
                    $"Redis returned an unknown media upload rate-limit outcome: {unknown}."),
            };
        }
        catch (Exception exception)
            when (exception is RedisException or WaitingRoomUnavailableException)
        {
            throw new MediaUploadIssueRateLimitUnavailableException(
                "The media upload issue rate limiter is unavailable.",
                exception);
        }
    }

    private static long ToMilliseconds(TimeSpan value) =>
        checked((long)value.TotalMilliseconds);
}
