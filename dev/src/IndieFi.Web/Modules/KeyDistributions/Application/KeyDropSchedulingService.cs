using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public enum ScheduleKeyDropOutcome
{
    Scheduled,
    GameNotFound,
    NotOwner,
    Invalid,
    AccountUnavailable,
}

public sealed record ScheduleKeyDropResult(
    ScheduleKeyDropOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations,
    long? KeyDropId = null,
    int KeyCount = 0);

public sealed class KeyDropSchedulingService
{
    private const string UniqueViolation = "23505";
    private const string FingerprintUniqueIndex =
        "IX_game_key_fingerprints_key_id_value";

    public const string OpensAtField = "OpensAt";
    public const string KeysField = "KeysText";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly GameKeyProtectionService _gameKeyProtection;

    public KeyDropSchedulingService(
        AppDbContext db,
        GameKeyProtectionService gameKeyProtection)
    {
        _db = db;
        _gameKeyProtection = gameKeyProtection;
    }

    public async Task<ScheduleKeyDropResult> ScheduleAsync(
        long gameId,
        long ownerId,
        DateTimeOffset? opensAt,
        string? keysText)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (!await AccountMutationGuard.LockActiveAsync(_db, ownerId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new ScheduleKeyDropResult(
                ScheduleKeyDropOutcome.AccountUnavailable,
                NoViolations);
        }

        var gameOwnerId = await _db.Games
            .Where(game => game.Id == gameId)
            .Select(game => (long?)game.OwnerId)
            .SingleOrDefaultAsync();

        if (gameOwnerId is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new ScheduleKeyDropResult(
                ScheduleKeyDropOutcome.GameNotFound,
                NoViolations);
        }

        if (gameOwnerId != ownerId)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new ScheduleKeyDropResult(
                ScheduleKeyDropOutcome.NotOwner,
                NoViolations);
        }

        var parsedKeys = GameKeyPolicy.ParseBatch(keysText);
        var keys = parsedKeys.Keys;

        // Both rules are checked before returning so a form with two mistakes reports
        // both at once rather than one per submission.
        var violations = new List<ValidationViolation>();

        foreach (var keyViolation in parsedKeys.Violations)
        {
            violations.Add(new ValidationViolation(
                KeysField,
                GetKeyViolationMessage(keyViolation)));
        }

        if (opensAt is null)
        {
            violations.Add(new ValidationViolation(
                OpensAtField,
                "오픈 시각을 입력해 주세요."));
        }
        else if (opensAt <= DateTimeOffset.Now)
        {
            violations.Add(new ValidationViolation(
                OpensAtField,
                "오픈 시각은 현재보다 미래여야 합니다."));
        }

        if (violations.Count > 0)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new ScheduleKeyDropResult(ScheduleKeyDropOutcome.Invalid, violations);
        }

        if (await ContainsRegisteredKeyAsync(keys))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return DuplicateKeyResult();
        }

        var keyDrop = new KeyDrop
        {
            GameId = gameId,
            OwnerId = ownerId,
            OpensAt = opensAt!.Value.ToUniversalTime(),
            TotalKeys = keys.Count,
        };

        var gameKeys = keys.Select(key =>
        {
            var protectedKey = _gameKeyProtection.Protect(key);
            var gameKey = new GameKey
            {
                Drop = keyDrop,
                Ciphertext = protectedKey.Ciphertext,
                Nonce = protectedKey.Nonce,
                AuthenticationTag = protectedKey.AuthenticationTag,
                EncryptionKeyId = protectedKey.EncryptionKeyId,
            };

            foreach (var fingerprint in
                     _gameKeyProtection.CreateWritableFingerprints(key))
            {
                _db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
                {
                    GameKey = gameKey,
                    KeyId = fingerprint.KeyId,
                    Value = fingerprint.Value,
                });
            }

            return gameKey;
        }).ToArray();

        _db.KeyDrops.Add(keyDrop);
        _db.GameKeys.AddRange(gameKeys);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: FingerprintUniqueIndex,
            })
        {
            await AccountMutationGuard.RollbackIfOwnedAsync(transaction);
            _db.ChangeTracker.Clear();
            return DuplicateKeyResult();
        }

        await AccountMutationGuard.CommitIfOwnedAsync(transaction);

        return new ScheduleKeyDropResult(
            ScheduleKeyDropOutcome.Scheduled,
            NoViolations,
            keyDrop.Id,
            keys.Count);
    }

    private async Task<bool> ContainsRegisteredKeyAsync(
        IReadOnlyList<GameKeyValue> keys)
    {
        var plaintextValues = keys.Select(key => key.Value).ToArray();
        if (await _db.GameKeys
            .AsNoTracking()
            .AnyAsync(key =>
                key.KeyValue != null && plaintextValues.Contains(key.KeyValue)))
        {
            return true;
        }

        var readableFingerprints = keys
            .SelectMany(_gameKeyProtection.CreateReadableFingerprints)
            .GroupBy(fingerprint => fingerprint.KeyId, StringComparer.Ordinal);

        foreach (var version in readableFingerprints)
        {
            var valuesParameter = new NpgsqlParameter(
                "values",
                version.Select(fingerprint => fingerprint.Value).ToArray())
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            };
            var keyIdParameter = new NpgsqlParameter("keyId", version.Key);
            var exists = await _db.Database
                .SqlQueryRaw<bool>(
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM game_key_fingerprints
                        WHERE key_id = @keyId
                          AND value = ANY(@values)) AS "Value"
                    """,
                    keyIdParameter,
                    valuesParameter)
                .SingleAsync();

            if (exists)
            {
                return true;
            }
        }

        return false;
    }

    private static ScheduleKeyDropResult DuplicateKeyResult() => new(
        ScheduleKeyDropOutcome.Invalid,
        [new ValidationViolation(KeysField, "이미 등록된 게임 키가 포함되어 있습니다.")]);

    private static string GetKeyViolationMessage(GameKeyInputViolation violation)
    {
        return violation.Error switch
        {
            GameKeyInputError.Empty => "키를 한 개 이상 입력해 주세요.",
            GameKeyInputError.TooLong =>
                $"{violation.LineNumber}번째 줄 키는 {GameKeyPolicy.MaxLength}자 이하여야 합니다.",
            GameKeyInputError.ContainsControlCharacter =>
                $"{violation.LineNumber}번째 줄 키에 사용할 수 없는 제어 문자가 있습니다.",
            GameKeyInputError.Duplicate =>
                $"{violation.LineNumber}번째 줄에 앞에서 입력한 것과 같은 키가 있습니다.",
            _ => throw new InvalidOperationException(
                $"Unsupported game key input error: {violation.Error}"),
        };
    }
}
