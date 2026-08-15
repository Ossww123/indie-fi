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
    Conflict,
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
    private readonly KeyDropMutationLock _keyDropMutationLock;

    public KeyDropInventoryService(
        AppDbContext db,
        GameKeyRegistrationWriter gameKeyRegistrationWriter,
        KeyDropMutationLock keyDropMutationLock)
    {
        _db = db;
        _gameKeyRegistrationWriter = gameKeyRegistrationWriter;
        _keyDropMutationLock = keyDropMutationLock;
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
        var state = await _keyDropMutationLock.LockAsync(
            dropId,
            ownerId,
            cancellationToken);
        if (!state.AccountAvailable)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new AddKeyInventoryResult(
                AddKeyInventoryOutcome.AccountUnavailable,
                NoViolations);
        }

        if (!state.DropExists)
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

        var status = state.GetRequiredStatus();
        if (!KeyDropStatusPolicy.CanAddInventory(status))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new AddKeyInventoryResult(
                AddKeyInventoryOutcome.InvalidStatus,
                NoViolations,
                TotalKeys: state.TotalKeys!.Value);
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
                TotalKeys: state.TotalKeys!.Value);
        }

        if (await _gameKeyRegistrationWriter.ContainsRegisteredKeyAsync(
                parsedKeys.Keys,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return DuplicateKeyResult(state.TotalKeys!.Value);
        }

        _gameKeyRegistrationWriter.StageForExistingDrop(
            dropId,
            parsedKeys.Keys);

        try
        {
            var updatedDropCount = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE key_drops
                SET total_keys = total_keys + {parsedKeys.Keys.Count},
                    state_version = state_version + 1
                WHERE id = {dropId}
                  AND state_version = {state.StateVersion!.Value}
                  AND closed_at IS NULL
                """,
                cancellationToken);
            if (updatedDropCount != 1)
            {
                await AccountMutationGuard.RollbackIfOwnedAsync(
                    transaction,
                    cancellationToken);
                _db.ChangeTracker.Clear();
                return new AddKeyInventoryResult(
                    AddKeyInventoryOutcome.Conflict,
                    NoViolations,
                    TotalKeys: state.TotalKeys!.Value);
            }

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
            return DuplicateKeyResult(state.TotalKeys!.Value);
        }

        await AccountMutationGuard.CommitIfOwnedAsync(
            transaction,
            cancellationToken);

        return new AddKeyInventoryResult(
            AddKeyInventoryOutcome.Added,
            NoViolations,
            parsedKeys.Keys.Count,
            state.TotalKeys!.Value + parsedKeys.Keys.Count);
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
}
