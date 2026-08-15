using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public enum AddKeyInventoryOutcome
{
    Added,
    DropNotFound,
    NotOwner,
    Invalid,
    InvalidStatus,
    AccountUnavailable,
}

public sealed record AddKeyInventoryResult(
    AddKeyInventoryOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations,
    int AddedCount = 0,
    int TotalKeys = 0);

public sealed class KeyDropInventoryService
{
    public const string KeysField = "KeysText";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly GameKeyRegistrationWriter _gameKeyRegistrationWriter;

    public KeyDropInventoryService(
        AppDbContext db,
        GameKeyRegistrationWriter gameKeyRegistrationWriter)
    {
        _db = db;
        _gameKeyRegistrationWriter = gameKeyRegistrationWriter;
    }

    public async Task<AddKeyInventoryResult> AddAsync(
        long dropId,
        long ownerId,
        string? keysText,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                ownerId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new AddKeyInventoryResult(
                AddKeyInventoryOutcome.AccountUnavailable,
                NoViolations);
        }

        var states = await _db.Database
            .SqlQuery<LockedDropState>($$"""
                SELECT key_drop.owner_id AS "OwnerId",
                       key_drop.opens_at AS "OpensAt",
                       key_drop.total_keys AS "TotalKeys",
                       (
                           SELECT COUNT(*)::integer
                           FROM game_keys AS game_key
                           WHERE game_key.drop_id = key_drop.id
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM key_grants AS grant_record
                                 WHERE grant_record.key_id = game_key.id
                                   AND grant_record.drop_id = key_drop.id)
                       ) AS "RemainingKeys",
                       key_drop.closed_at AS "ClosedAt",
                       now() AS "DatabaseNow"
                FROM key_drops AS key_drop
                WHERE key_drop.id = {{dropId}}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        var state = states.SingleOrDefault();

        if (state is null)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new AddKeyInventoryResult(
                AddKeyInventoryOutcome.DropNotFound,
                NoViolations);
        }

        if (state.OwnerId != ownerId)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new AddKeyInventoryResult(
                AddKeyInventoryOutcome.NotOwner,
                NoViolations);
        }

        var status = KeyDropStatusPolicy.Resolve(
            state.OpensAt,
            state.TotalKeys,
            state.RemainingKeys,
            state.ClosedAt,
            state.DatabaseNow);
        if (!KeyDropStatusPolicy.CanAddInventory(status))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new AddKeyInventoryResult(
                AddKeyInventoryOutcome.InvalidStatus,
                NoViolations,
                TotalKeys: state.TotalKeys);
        }

        var parsedKeys = GameKeyPolicy.ParseBatch(keysText);
        if (!parsedKeys.IsValid)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new AddKeyInventoryResult(
                AddKeyInventoryOutcome.Invalid,
                parsedKeys.Violations
                    .Select(violation => new ValidationViolation(
                        KeysField,
                        GetKeyViolationMessage(violation)))
                    .ToArray(),
                TotalKeys: state.TotalKeys);
        }

        if (await _gameKeyRegistrationWriter.ContainsRegisteredKeyAsync(
                parsedKeys.Keys,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return DuplicateKeyResult(state.TotalKeys);
        }

        _gameKeyRegistrationWriter.StageForExistingDrop(
            dropId,
            parsedKeys.Keys);

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE key_drops
                SET total_keys = total_keys + {parsedKeys.Keys.Count}
                WHERE id = {dropId}
                """,
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (GameKeyRegistrationWriter
                .IsDuplicateFingerprintViolation(exception))
        {
            await AccountMutationGuard.RollbackIfOwnedAsync(
                transaction,
                cancellationToken);
            _db.ChangeTracker.Clear();
            return DuplicateKeyResult(state.TotalKeys);
        }

        await AccountMutationGuard.CommitIfOwnedAsync(
            transaction,
            cancellationToken);

        return new AddKeyInventoryResult(
            AddKeyInventoryOutcome.Added,
            NoViolations,
            parsedKeys.Keys.Count,
            state.TotalKeys + parsedKeys.Keys.Count);
    }

    private static AddKeyInventoryResult DuplicateKeyResult(int totalKeys) => new(
        AddKeyInventoryOutcome.Invalid,
        [new ValidationViolation(KeysField, "이미 등록된 게임 키가 포함되어 있습니다.")],
        TotalKeys: totalKeys);

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

    private sealed record LockedDropState
    {
        public long OwnerId { get; init; }

        public DateTimeOffset OpensAt { get; init; }

        public int TotalKeys { get; init; }

        public int RemainingKeys { get; init; }

        public DateTimeOffset? ClosedAt { get; init; }

        public DateTimeOffset DatabaseNow { get; init; }
    }
}
