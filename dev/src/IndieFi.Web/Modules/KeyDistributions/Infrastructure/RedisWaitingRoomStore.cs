using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public sealed class RedisWaitingRoomStore : IWaitingRoomStore
{
    private const string JoinScript = """
        local function now_milliseconds()
            local current = redis.call('TIME')
            return (tonumber(current[1]) * 1000) + math.floor(tonumber(current[2]) / 1000)
        end

        local function split_entry(value)
            local parts = {}
            for part in string.gmatch(value .. '|', '(.-)|') do
                table.insert(parts, part)
            end
            return parts
        end

        local function refresh_ttl(ttl)
            for index = 1, #KEYS do
                if redis.call('EXISTS', KEYS[index]) == 1 then
                    redis.call('PEXPIRE', KEYS[index], ttl)
                end
            end
        end

        if redis.call('HGET', KEYS[1], 'terminal') == '1' then
            return {3, ''}
        end

        local user_id = ARGV[1]
        local existing = redis.call('HGET', KEYS[2], user_id)
        local now = now_milliseconds()
        local ttl = tonumber(ARGV[3])
        if existing then
            local parts = split_entry(existing)
            local state = tonumber(parts[2])
            local within_grace = now - tonumber(parts[4]) <= tonumber(ARGV[4])
            local admission_valid = state == 3
                and parts[5] ~= ''
                and now < tonumber(parts[5])
            if state == 4 or state == 6 or admission_valid
                or (state ~= 5 and within_grace) then
                parts[4] = tostring(now)
                existing = table.concat(parts, '|')
                redis.call('HSET', KEYS[2], user_id, existing)
                refresh_ttl(ttl)
                return {2, existing}
            end

            redis.call('ZREM', KEYS[3], user_id)
            redis.call('ZREM', KEYS[4], user_id)
            redis.call('ZREM', KEYS[5], user_id)
            local order = tonumber(parts[3])
            local next_state = 1
            local target_key = KEYS[3]
            if ARGV[2] == '2' then
                order = redis.call('HINCRBY', KEYS[1], 'next_order', 1)
                next_state = 2
                target_key = KEYS[4]
            end

            existing = '1|' .. next_state .. '|' .. order .. '|' .. now .. '|'
            redis.call('HSET', KEYS[2], user_id, existing)
            redis.call('ZADD', target_key, order, user_id)
            refresh_ttl(ttl)
            return {4, existing}
        end

        local order = redis.call('HINCRBY', KEYS[1], 'next_order', 1)
        local state = 1
        local target_key = KEYS[3]
        if ARGV[2] == '2' then
            state = 2
            target_key = KEYS[4]
        end

        local entry = '1|' .. state .. '|' .. order .. '|' .. now .. '|'
        redis.call('HSET', KEYS[2], user_id, entry)
        redis.call('ZADD', target_key, order, user_id)
        refresh_ttl(ttl)
        return {1, entry}
        """;

    private const string TransitionScript = """
        local function now_milliseconds()
            local current = redis.call('TIME')
            return (tonumber(current[1]) * 1000) + math.floor(tonumber(current[2]) / 1000)
        end

        local function split_entry(value)
            local parts = {}
            for part in string.gmatch(value .. '|', '(.-)|') do
                table.insert(parts, part)
            end
            return parts
        end

        local function refresh_ttl(ttl)
            for index = 1, #KEYS do
                if redis.call('EXISTS', KEYS[index]) == 1 then
                    redis.call('PEXPIRE', KEYS[index], ttl)
                end
            end
        end

        if redis.call('HGET', KEYS[1], 'terminal') == '1' then
            return {4, ''}
        end

        local user_id = ARGV[1]
        local existing = redis.call('HGET', KEYS[2], user_id)
        if not existing then
            return {2, ''}
        end

        local parts = split_entry(existing)
        if parts[2] ~= ARGV[2] then
            return {3, existing}
        end

        local now = now_milliseconds()
        local target_state = tonumber(ARGV[3])
        local admission_expires = ''
        redis.call('ZREM', KEYS[3], user_id)
        redis.call('ZREM', KEYS[4], user_id)
        redis.call('ZREM', KEYS[5], user_id)

        if target_state == 1 then
            redis.call('ZADD', KEYS[3], tonumber(parts[3]), user_id)
        elseif target_state == 2 then
            redis.call('ZADD', KEYS[4], tonumber(parts[3]), user_id)
        elseif target_state == 3 then
            admission_expires = tostring(now + tonumber(ARGV[5]))
            redis.call('ZADD', KEYS[5], tonumber(admission_expires), user_id)
        end

        local entry = '1|' .. target_state .. '|' .. parts[3] .. '|' .. now .. '|' .. admission_expires
        redis.call('HSET', KEYS[2], user_id, entry)
        refresh_ttl(tonumber(ARGV[4]))
        return {1, entry}
        """;

    private readonly WaitingRoomRedisConnection _connection;
    private readonly WaitingRoomRedisKeys _keys;
    private readonly WaitingRoomPolicyOptions _policy;

    public RedisWaitingRoomStore(
        WaitingRoomRedisConnection connection,
        WaitingRoomRedisKeys keys,
        IOptions<WaitingRoomPolicyOptions> policy)
    {
        _connection = connection;
        _keys = keys;
        _policy = policy.Value;
    }

    public async Task<WaitingRoomJoinResult> JoinAsync(
        WaitingRoomJoinCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(command.DropId, command.UserId);
        if (!Enum.IsDefined(command.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var result = await EvaluateAsync(
            JoinScript,
            _keys.All(command.DropId),
            [
                command.UserId,
                command.Mode == WaitingRoomJoinMode.PreOpen ? 1 : 2,
                ToMilliseconds(_policy.ActiveStateTtl),
                ToMilliseconds(_policy.ReconnectGrace),
            ],
            cancellationToken);
        var parts = GetResultParts(result);
        var outcome = (int)parts[0] switch
        {
            1 => WaitingRoomJoinOutcome.Created,
            2 => WaitingRoomJoinOutcome.Existing,
            3 => WaitingRoomJoinOutcome.Terminal,
            4 => WaitingRoomJoinOutcome.Rejoined,
            var value => throw new InvalidOperationException(
                $"Redis returned an unknown waiting-room join outcome: {value}."),
        };
        var entry = outcome == WaitingRoomJoinOutcome.Terminal
            ? null
            : WaitingRoomRedisValueCodec.Deserialize((RedisValue)parts[1]);
        return new WaitingRoomJoinResult(outcome, entry);
    }

    public async Task<WaitingRoomStoredEntry?> GetEntryAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(dropId, userId);
        var database = await GetDatabaseAsync(cancellationToken);
        try
        {
            var value = await database.HashGetAsync(
                _keys.Entries(dropId),
                userId,
                CommandFlags.DemandMaster);
            return value.IsNull
                ? null
                : WaitingRoomRedisValueCodec.Deserialize(value);
        }
        catch (RedisException exception)
        {
            throw Unavailable(exception);
        }
    }

    public async Task<WaitingRoomTransitionResult> TransitionAsync(
        WaitingRoomTransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(command.DropId, command.UserId);
        if (!Enum.IsDefined(command.ExpectedState)
            || !Enum.IsDefined(command.TargetState)
            || command.ExpectedState == command.TargetState)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var result = await EvaluateAsync(
            TransitionScript,
            _keys.All(command.DropId),
            [
                command.UserId,
                (int)command.ExpectedState,
                (int)command.TargetState,
                ToMilliseconds(_policy.ActiveStateTtl),
                ToMilliseconds(_policy.AdmissionLifetime),
            ],
            cancellationToken);
        var parts = GetResultParts(result);
        var outcome = (int)parts[0] switch
        {
            1 => WaitingRoomTransitionOutcome.Changed,
            2 => WaitingRoomTransitionOutcome.Missing,
            3 => WaitingRoomTransitionOutcome.StateMismatch,
            4 => WaitingRoomTransitionOutcome.Terminal,
            var value => throw new InvalidOperationException(
                $"Redis returned an unknown waiting-room transition outcome: {value}."),
        };
        var entry = outcome is WaitingRoomTransitionOutcome.Missing
            or WaitingRoomTransitionOutcome.Terminal
            ? null
            : WaitingRoomRedisValueCodec.Deserialize((RedisValue)parts[1]);
        return new WaitingRoomTransitionResult(outcome, entry);
    }

    private async Task<RedisResult> EvaluateAsync(
        string script,
        RedisKey[] keys,
        RedisValue[] values,
        CancellationToken cancellationToken)
    {
        var database = await GetDatabaseAsync(cancellationToken);
        try
        {
            return await database.ScriptEvaluateAsync(
                script,
                keys,
                values,
                CommandFlags.DemandMaster);
        }
        catch (RedisException exception)
        {
            throw Unavailable(exception);
        }
    }

    private async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await _connection.GetAsync(cancellationToken);
        return multiplexer.GetDatabase();
    }

    private static long ToMilliseconds(TimeSpan value) =>
        checked((long)value.TotalMilliseconds);

    private static RedisResult[] GetResultParts(RedisResult result)
    {
        var parts = (RedisResult[]?)result;
        if (parts is not { Length: 2 })
        {
            throw new InvalidOperationException(
                "Redis returned an invalid waiting-room script result.");
        }

        return parts;
    }

    private static void ValidateIdentity(long dropId, long userId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
    }

    private static WaitingRoomUnavailableException Unavailable(
        RedisException exception) =>
        new("The virtual waiting-room Redis operation failed.", exception);
}
