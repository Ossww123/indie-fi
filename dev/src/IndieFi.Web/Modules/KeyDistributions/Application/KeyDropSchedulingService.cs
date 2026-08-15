using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;

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
    public const string OpensAtField = "OpensAt";
    public const string KeysField = "KeysText";
    public const string MinimumAccountAgeDaysField = "MinimumAccountAgeDays";
    public const string MinimumCompletedReviewCountField =
        "MinimumCompletedReviewCount";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly GameKeyRegistrationWriter _gameKeyRegistrationWriter;

    public KeyDropSchedulingService(
        AppDbContext db,
        GameKeyRegistrationWriter gameKeyRegistrationWriter)
    {
        _db = db;
        _gameKeyRegistrationWriter = gameKeyRegistrationWriter;
    }

    public async Task<ScheduleKeyDropResult> ScheduleAsync(
        long gameId,
        long ownerId,
        DateTimeOffset? opensAt,
        string? keysText,
        int? minimumAccountAgeDays = null,
        int? minimumCompletedReviewCount = null)
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

        if (!KeyDropEligibilityPolicy.IsValidAccountAgeDays(
                minimumAccountAgeDays))
        {
            violations.Add(new ValidationViolation(
                MinimumAccountAgeDaysField,
                $"계정 경과 기간은 비워 두거나 {KeyDropEligibilityPolicy.MinimumAccountAgeDays}~{KeyDropEligibilityPolicy.MaximumAccountAgeDays}일로 입력해 주세요."));
        }

        if (!KeyDropEligibilityPolicy.IsValidCompletedReviewCount(
                minimumCompletedReviewCount))
        {
            violations.Add(new ValidationViolation(
                MinimumCompletedReviewCountField,
                $"완료 리뷰 수는 비워 두거나 {KeyDropEligibilityPolicy.MinimumCompletedReviewCount}~{KeyDropEligibilityPolicy.MaximumCompletedReviewCount}개로 입력해 주세요."));
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

        if (await _gameKeyRegistrationWriter.ContainsRegisteredKeyAsync(keys))
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
            MinimumAccountAgeDays = minimumAccountAgeDays,
            MinimumCompletedReviewCount = minimumCompletedReviewCount,
        };

        _db.KeyDrops.Add(keyDrop);
        _gameKeyRegistrationWriter.StageForNewDrop(keyDrop, keys);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
            when (GameKeyRegistrationWriter
                .IsDuplicateFingerprintViolation(exception))
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
