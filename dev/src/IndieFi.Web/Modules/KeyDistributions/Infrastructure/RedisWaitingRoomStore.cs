using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Infrastructure.Observability;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Security.Cryptography;
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

    private const string FinalizeOrderScript = """
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
            return {3, 0}
        end

        if redis.call('HGET', KEYS[1], 'order_finalized') == '1' then
            return {2, redis.call('ZCARD', KEYS[4])}
        end

        local pre_users = redis.call('ZRANGE', KEYS[3], 0, -1)
        local tail_users = redis.call('ZRANGE', KEYS[4], 0, -1)
        local randomized = {}
        for _, user_id in ipairs(pre_users) do
            local digest = redis.sha1hex(ARGV[1] .. ':' .. user_id)
            table.insert(randomized, {
                user_id = user_id,
                score = tonumber(string.sub(digest, 1, 13), 16)
            })
        end
        table.sort(randomized, function(left, right)
            if left.score == right.score then
                return left.user_id < right.user_id
            end
            return left.score < right.score
        end)

        redis.call('DEL', KEYS[4])
        local seen = {}
        local order = 0
        local function append_user(user_id)
            if seen[user_id] then
                return
            end
            local existing = redis.call('HGET', KEYS[2], user_id)
            if not existing then
                return
            end
            seen[user_id] = true
            order = order + 1
            local parts = split_entry(existing)
            local updated = '1|2|' .. order .. '|' .. parts[4] .. '|'
            redis.call('HSET', KEYS[2], user_id, updated)
            redis.call('ZADD', KEYS[4], order, user_id)
        end

        for _, ranked in ipairs(randomized) do
            append_user(ranked.user_id)
        end
        for _, user_id in ipairs(tail_users) do
            append_user(user_id)
        end

        redis.call('DEL', KEYS[3])
        redis.call('HSET', KEYS[1], 'next_order', order, 'order_finalized', 1)
        refresh_ttl(tonumber(ARGV[2]))
        return {1, order}
        """;

    private const string SnapshotScript = """
        local entry = redis.call('HGET', KEYS[2], ARGV[1])
        local waiting_count = redis.call('ZCARD', KEYS[4])
        local pre_count = redis.call('ZCARD', KEYS[3])
        local admitted_count = redis.call('ZCARD', KEYS[5])
        local finalized = redis.call('HGET', KEYS[1], 'order_finalized') == '1' and 1 or 0
        if not entry then
            return {0, '', -1, -1, waiting_count, pre_count, admitted_count, finalized}
        end

        local state = tonumber(string.match(entry, '^1|(%d+)|'))
        local position = -1
        local ahead = -1
        if state == 2 then
            local rank = redis.call('ZRANK', KEYS[4], ARGV[1])
            if rank then
                ahead = rank
                position = rank + 1
            end
        end
        return {1, entry, position, ahead, waiting_count, pre_count, admitted_count, finalized}
        """;

    private const string AdvanceAdmissionsScript = """
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
            return {2, 0, '', ''}
        end

        local now = now_milliseconds()
        local expired = {}
        local expired_users = redis.call('ZRANGEBYSCORE', KEYS[5], '-inf', now)
        for _, user_id in ipairs(expired_users) do
            redis.call('ZREM', KEYS[5], user_id)
            local existing = redis.call('HGET', KEYS[2], user_id)
            if existing then
                local parts = split_entry(existing)
                if tonumber(parts[2]) == 3 and parts[5] ~= '' and tonumber(parts[5]) <= now then
                    local updated = '1|5|' .. parts[3] .. '|' .. parts[4] .. '|'
                    redis.call('HSET', KEYS[2], user_id, updated)
                    table.insert(expired, user_id)
                end
            end
        end

        local limit = tonumber(ARGV[1])
        local admitted_count = redis.call('ZCARD', KEYS[5])
        local slots = limit - admitted_count
        local promoted = {}
        if slots > 0 then
            local admission_expires = now + tonumber(ARGV[2])
            while #promoted < slots do
                local candidates = redis.call('ZRANGE', KEYS[4], 0, 0)
                if #candidates == 0 then
                    break
                end
                local user_id = candidates[1]
                local existing = redis.call('HGET', KEYS[2], user_id)
                if existing then
                    local parts = split_entry(existing)
                    if tonumber(parts[2]) == 2 then
                        redis.call('ZREM', KEYS[4], user_id)
                        redis.call('ZADD', KEYS[5], admission_expires, user_id)
                        local updated = '1|3|' .. parts[3] .. '|' .. parts[4] .. '|' .. admission_expires
                        redis.call('HSET', KEYS[2], user_id, updated)
                        table.insert(promoted, user_id)
                    else
                        redis.call('ZREM', KEYS[4], user_id)
                    end
                else
                    redis.call('ZREM', KEYS[4], user_id)
                end
            end
        end

        admitted_count = redis.call('ZCARD', KEYS[5])
        refresh_ttl(tonumber(ARGV[3]))
        return {1, admitted_count, table.concat(promoted, ','), table.concat(expired, ',')}
        """;

    private const string ValidateAdmissionScript = """
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
            return {5, ''}
        end

        local user_id = ARGV[1]
        local existing = redis.call('HGET', KEYS[2], user_id)
        if not existing then
            return {2, ''}
        end

        local parts = split_entry(existing)
        if tonumber(parts[2]) ~= 3 then
            return {3, existing}
        end

        local now = now_milliseconds()
        local expires = tonumber(parts[5])
        if not expires or expires <= now then
            redis.call('ZREM', KEYS[5], user_id)
            local expired = '1|5|' .. parts[3] .. '|' .. parts[4] .. '|'
            redis.call('HSET', KEYS[2], user_id, expired)
            refresh_ttl(tonumber(ARGV[2]))
            return {4, expired}
        end

        refresh_ttl(tonumber(ARGV[2]))
        return {1, existing}
        """;

    private const string CompleteScript = """
        local function split_entry(value)
            local parts = {}
            for part in string.gmatch(value .. '|', '(.-)|') do
                table.insert(parts, part)
            end
            return parts
        end

        local existing = redis.call('HGET', KEYS[2], ARGV[1])
        if not existing then
            return {3, ''}
        end

        local parts = split_entry(existing)
        if tonumber(parts[2]) == 4 then
            return {2, existing}
        end

        redis.call('ZREM', KEYS[3], ARGV[1])
        redis.call('ZREM', KEYS[4], ARGV[1])
        redis.call('ZREM', KEYS[5], ARGV[1])
        local completed = '1|4|' .. parts[3] .. '|' .. parts[4] .. '|'
        redis.call('HSET', KEYS[2], ARGV[1], completed)
        for index = 1, #KEYS do
            if redis.call('EXISTS', KEYS[index]) == 1 then
                redis.call('PEXPIRE', KEYS[index], tonumber(ARGV[2]))
            end
        end
        return {1, completed}
        """;

    private const string TerminateScript = """
        local function split_entry(value)
            local parts = {}
            for part in string.gmatch(value .. '|', '(.-)|') do
                table.insert(parts, part)
            end
            return parts
        end

        if redis.call('HGET', KEYS[1], 'terminal') == '1' then
            return {2, ''}
        end

        local affected = {}
        local entries = redis.call('HGETALL', KEYS[2])
        for index = 1, #entries, 2 do
            local user_id = entries[index]
            local value = entries[index + 1]
            local parts = split_entry(value)
            if tonumber(parts[2]) ~= 4 then
                local terminal = '1|6|' .. parts[3] .. '|' .. parts[4] .. '|'
                redis.call('HSET', KEYS[2], user_id, terminal)
                table.insert(affected, user_id)
            end
        end

        redis.call('DEL', KEYS[3], KEYS[4], KEYS[5])
        redis.call('HSET', KEYS[1], 'terminal', '1')
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[1]))
        if redis.call('EXISTS', KEYS[2]) == 1 then
            redis.call('PEXPIRE', KEYS[2], tonumber(ARGV[1]))
        end
        return {1, table.concat(affected, ',')}
        """;

    private readonly WaitingRoomRedisConnection _connection;
    private readonly WaitingRoomRedisKeys _keys;
    private readonly WaitingRoomPolicyOptions _policy;
    private readonly ILogger<RedisWaitingRoomStore> _logger;

    public RedisWaitingRoomStore(
        WaitingRoomRedisConnection connection,
        WaitingRoomRedisKeys keys,
        IOptions<WaitingRoomPolicyOptions> policy,
        ILogger<RedisWaitingRoomStore>? logger = null)
    {
        _connection = connection;
        _keys = keys;
        _policy = policy.Value;
        _logger = logger ?? NullLogger<RedisWaitingRoomStore>.Instance;
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
            "join",
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
        WaitingRoomTelemetry.RecordOperation("join", outcome.ToString());
        return new WaitingRoomJoinResult(outcome, entry);
    }

    public async Task<WaitingRoomStoredEntry?> GetEntryAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(dropId, userId);
        using var activity = StartRedisActivity("get_entry");
        var started = Stopwatch.GetTimestamp();
        try
        {
            var database = await GetDatabaseAsync(cancellationToken);
            var value = await database.HashGetAsync(
                _keys.Entries(dropId),
                userId,
                CommandFlags.DemandMaster);
            return value.IsNull
                ? null
                : WaitingRoomRedisValueCodec.Deserialize(value);
        }
        catch (Exception exception)
            when (exception is RedisException
                or RedisTimeoutException
                or ObjectDisposedException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            RecordRedisFailure("get_entry", exception);
            throw Unavailable(exception);
        }
        catch (WaitingRoomUnavailableException exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            RecordRedisFailure("get_entry", exception);
            throw;
        }
        finally
        {
            WaitingRoomTelemetry.RecordRedisDuration(
                "get_entry",
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<WaitingRoomSnapshot?> GetSnapshotAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(dropId, userId);
        var result = await EvaluateAsync(
            "snapshot",
            SnapshotScript,
            _keys.All(dropId),
            [userId],
            cancellationToken);
        var parts = GetResultParts(result, expectedLength: 8);
        if ((int)parts[0] == 0)
        {
            WaitingRoomTelemetry.RecordOperation("snapshot", "Missing");
            return null;
        }

        var position = (int)parts[2];
        var aheadCount = (int)parts[3];
        WaitingRoomTelemetry.RecordOperation("snapshot", "Found");
        return new WaitingRoomSnapshot(
            WaitingRoomRedisValueCodec.Deserialize((RedisValue)parts[1]),
            position < 0 ? null : position,
            aheadCount < 0 ? null : aheadCount,
            checked((int)(long)parts[4]),
            checked((int)(long)parts[5]),
            checked((int)(long)parts[6]),
            (int)parts[7] == 1);
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
            "transition",
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
        WaitingRoomTelemetry.RecordOperation("transition", outcome.ToString());
        if (outcome == WaitingRoomTransitionOutcome.Changed)
        {
            WaitingRoomTelemetry.RecordTransition(command.TargetState.ToString());
        }

        return new WaitingRoomTransitionResult(outcome, entry);
    }

    public async Task<WaitingRoomOrderFinalizationResult> FinalizeOrderAsync(
        long dropId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        var seed = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var result = await EvaluateAsync(
            "finalize_order",
            FinalizeOrderScript,
            _keys.All(dropId),
            [seed, ToMilliseconds(_policy.ActiveStateTtl)],
            cancellationToken);
        var parts = GetResultParts(result);
        var outcome = (int)parts[0] switch
        {
            1 => WaitingRoomOrderFinalizationOutcome.Finalized,
            2 => WaitingRoomOrderFinalizationOutcome.AlreadyFinalized,
            3 => WaitingRoomOrderFinalizationOutcome.Terminal,
            var value => throw new InvalidOperationException(
                $"Redis returned an unknown waiting-room finalization outcome: {value}."),
        };
        WaitingRoomTelemetry.RecordOperation("finalize_order", outcome.ToString());
        return new WaitingRoomOrderFinalizationResult(
            outcome,
            checked((int)(long)parts[1]));
    }

    public async Task<WaitingRoomAdmissionAdvanceResult> AdvanceAdmissionsAsync(
        long dropId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        var result = await EvaluateAsync(
            "advance_admissions",
            AdvanceAdmissionsScript,
            _keys.All(dropId),
            [
                _policy.ConcurrentAdmissionLimit,
                ToMilliseconds(_policy.AdmissionLifetime),
                ToMilliseconds(_policy.ActiveStateTtl),
            ],
            cancellationToken);
        var parts = GetResultParts(result, expectedLength: 4);
        var outcome = (int)parts[0] switch
        {
            1 => WaitingRoomAdmissionAdvanceOutcome.Advanced,
            2 => WaitingRoomAdmissionAdvanceOutcome.Terminal,
            var value => throw new InvalidOperationException(
                $"Redis returned an unknown waiting-room admission outcome: {value}."),
        };
        var promoted = ParseUserIds((string?)parts[2]);
        var expired = ParseUserIds((string?)parts[3]);
        WaitingRoomTelemetry.RecordOperation("advance_admissions", outcome.ToString());
        WaitingRoomTelemetry.RecordTransition("promoted", promoted.Count);
        WaitingRoomTelemetry.RecordTransition("expired", expired.Count);
        return new WaitingRoomAdmissionAdvanceResult(
            outcome,
            promoted,
            expired,
            checked((int)(long)parts[1]));
    }

    public async Task<WaitingRoomAdmissionValidationResult> ValidateAdmissionAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(dropId, userId);
        var result = await EvaluateAsync(
            "validate_admission",
            ValidateAdmissionScript,
            _keys.All(dropId),
            [userId, ToMilliseconds(_policy.ActiveStateTtl)],
            cancellationToken);
        var parts = GetResultParts(result, expectedLength: 2);
        var outcome = (int)parts[0] switch
        {
            1 => WaitingRoomAdmissionValidationOutcome.Valid,
            2 => WaitingRoomAdmissionValidationOutcome.Missing,
            3 => WaitingRoomAdmissionValidationOutcome.NotAdmitted,
            4 => WaitingRoomAdmissionValidationOutcome.Expired,
            5 => WaitingRoomAdmissionValidationOutcome.Terminal,
            var unknown => throw new InvalidOperationException(
                $"Redis returned an unknown waiting-room admission validation outcome: {unknown}."),
        };
        var value = (RedisValue)parts[1];
        WaitingRoomTelemetry.RecordOperation("validate_admission", outcome.ToString());
        return new WaitingRoomAdmissionValidationResult(
            outcome,
            value.IsNullOrEmpty ? null : WaitingRoomRedisValueCodec.Deserialize(value));
    }

    public async Task<WaitingRoomCompletionResult> CompleteAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(dropId, userId);
        var result = await EvaluateAsync(
            "complete",
            CompleteScript,
            _keys.All(dropId),
            [userId, ToMilliseconds(_policy.ActiveStateTtl)],
            cancellationToken);
        var parts = GetResultParts(result, expectedLength: 2);
        var outcome = (int)parts[0] switch
        {
            1 => WaitingRoomCompletionOutcome.Completed,
            2 => WaitingRoomCompletionOutcome.AlreadyCompleted,
            3 => WaitingRoomCompletionOutcome.Missing,
            var unknown => throw new InvalidOperationException(
                $"Redis returned an unknown waiting-room completion outcome: {unknown}."),
        };
        var value = (RedisValue)parts[1];
        WaitingRoomTelemetry.RecordOperation("complete", outcome.ToString());
        if (outcome == WaitingRoomCompletionOutcome.Completed)
        {
            WaitingRoomTelemetry.RecordTransition("completed");
        }

        return new WaitingRoomCompletionResult(
            outcome,
            value.IsNullOrEmpty ? null : WaitingRoomRedisValueCodec.Deserialize(value));
    }

    public async Task<WaitingRoomTerminationResult> TerminateAsync(
        long dropId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        var result = await EvaluateAsync(
            "terminate",
            TerminateScript,
            _keys.All(dropId),
            [ToMilliseconds(_policy.TerminalStateTtl)],
            cancellationToken);
        var parts = GetResultParts(result, expectedLength: 2);
        var outcome = (int)parts[0] switch
        {
            1 => WaitingRoomTerminationOutcome.Terminated,
            2 => WaitingRoomTerminationOutcome.AlreadyTerminal,
            var unknown => throw new InvalidOperationException(
                $"Redis returned an unknown waiting-room termination outcome: {unknown}."),
        };
        var affectedUserIds = ParseUserIds((string?)parts[1]);
        WaitingRoomTelemetry.RecordOperation("terminate", outcome.ToString());
        WaitingRoomTelemetry.RecordTransition("terminal", affectedUserIds.Count);
        return new WaitingRoomTerminationResult(
            outcome,
            affectedUserIds);
    }

    private async Task<RedisResult> EvaluateAsync(
        string operation,
        string script,
        RedisKey[] keys,
        RedisValue[] values,
        CancellationToken cancellationToken)
    {
        using var activity = StartRedisActivity(operation);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var database = await GetDatabaseAsync(cancellationToken);
            return await database.ScriptEvaluateAsync(
                script,
                keys,
                values,
                CommandFlags.DemandMaster);
        }
        catch (Exception exception)
            when (exception is RedisException
                or RedisTimeoutException
                or ObjectDisposedException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            RecordRedisFailure(operation, exception);
            throw Unavailable(exception);
        }
        catch (WaitingRoomUnavailableException exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            RecordRedisFailure(operation, exception);
            throw;
        }
        finally
        {
            WaitingRoomTelemetry.RecordRedisDuration(
                operation,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await _connection.GetAsync(cancellationToken);
        return multiplexer.GetDatabase();
    }

    private static Activity? StartRedisActivity(string operation)
    {
        var activity = ObservabilityActivity.Source.StartActivity(
            $"redis.waiting_room.{operation}",
            ActivityKind.Client);
        activity?.SetTag("db.system.name", "redis");
        activity?.SetTag("db.operation.name", operation);
        return activity;
    }

    private static long ToMilliseconds(TimeSpan value) =>
        checked((long)value.TotalMilliseconds);

    private static RedisResult[] GetResultParts(
        RedisResult result,
        int expectedLength = 2)
    {
        var parts = (RedisResult[]?)result;
        if (parts is null || parts.Length != expectedLength)
        {
            throw new InvalidOperationException(
                "Redis returned an invalid waiting-room script result.");
        }

        return parts;
    }

    private static IReadOnlyList<long> ParseUserIds(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        return value.Split(',').Select(item => long.Parse(
            item,
            System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static void ValidateIdentity(long dropId, long userId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dropId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
    }

    private void RecordRedisFailure(string operation, Exception exception)
    {
        WaitingRoomTelemetry.RecordFailure("redis", operation);
        _logger.LogWarning(
            exception,
            "Waiting-room Redis operation failed operation={Operation}",
            operation);
    }

    private static WaitingRoomUnavailableException Unavailable(
        Exception exception) =>
        new("The virtual waiting-room Redis operation failed.", exception);

    private static class ObservabilityActivity
    {
        internal static readonly ActivitySource Source =
            new(ObservabilityActivitySources.Redis);
    }
}
