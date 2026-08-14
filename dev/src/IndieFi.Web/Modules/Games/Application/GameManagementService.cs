using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;

namespace IndieFi.Web.Modules.Games.Application;

public enum CreateGameOutcome
{
    Created,
    Invalid,
    AccountUnavailable,
}

public enum UpdateGameOutcome
{
    Updated,
    NotFound,
    Invalid,
    AccountUnavailable,
}

public sealed record CreateGameResult(
    CreateGameOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations,
    long? GameId = null,
    string? Title = null);

public sealed record UpdateGameResult(
    UpdateGameOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations);

public sealed class GameManagementService
{
    public const string TitleField = "Title";
    public const string DescriptionField = "Description";

    private const int TitleMaxLength = 200;
    private const int DescriptionMaxLength = 2000;

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;

    public GameManagementService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CreateGameResult> CreateAsync(
        long ownerId,
        string? title,
        string? description)
    {
        var violations = Validate(title, description);
        if (violations.Count > 0)
        {
            return new CreateGameResult(CreateGameOutcome.Invalid, violations);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (!await AccountMutationGuard.LockActiveAsync(_db, ownerId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new CreateGameResult(
                CreateGameOutcome.AccountUnavailable,
                NoViolations);
        }

        var game = new Game
        {
            OwnerId = ownerId,
            Title = title!.Trim(),
            Description = NormalizeDescription(description),
        };

        _db.Games.Add(game);
        await _db.SaveChangesAsync();
        await AccountMutationGuard.CommitIfOwnedAsync(transaction);

        return new CreateGameResult(
            CreateGameOutcome.Created,
            NoViolations,
            game.Id,
            game.Title);
    }

    public async Task<UpdateGameResult> UpdateAsync(
        long gameId,
        long ownerId,
        string? title,
        string? description)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db);
        if (!await AccountMutationGuard.LockActiveAsync(_db, ownerId))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new UpdateGameResult(
                UpdateGameOutcome.AccountUnavailable,
                NoViolations);
        }

        var game = await _db.Games.FindAsync(gameId);

        // Checked before validation so an unauthorized caller cannot tell a missing
        // game apart from one they do not own by comparing error messages.
        if (game is null || game.OwnerId != ownerId)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new UpdateGameResult(UpdateGameOutcome.NotFound, NoViolations);
        }

        var violations = Validate(title, description);
        if (violations.Count > 0)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction);
            return new UpdateGameResult(UpdateGameOutcome.Invalid, violations);
        }

        game.Title = title!.Trim();
        game.Description = NormalizeDescription(description);
        await _db.SaveChangesAsync();
        await AccountMutationGuard.CommitIfOwnedAsync(transaction);

        return new UpdateGameResult(UpdateGameOutcome.Updated, NoViolations);
    }

    /// <summary>
    /// Enforces the title and description rules for every caller. The database only
    /// rejects a null title, so a blank one would otherwise be stored.
    /// </summary>
    private static List<ValidationViolation> Validate(string? title, string? description)
    {
        var violations = new List<ValidationViolation>();

        if (string.IsNullOrWhiteSpace(title))
        {
            violations.Add(new ValidationViolation(
                TitleField,
                "게임 제목을 입력해 주세요."));
        }
        else if (title.Trim().Length > TitleMaxLength)
        {
            violations.Add(new ValidationViolation(
                TitleField,
                "제목이 너무 깁니다."));
        }

        if (description?.Length > DescriptionMaxLength)
        {
            violations.Add(new ValidationViolation(
                DescriptionField,
                "소개가 너무 깁니다."));
        }

        return violations;
    }

    private static string? NormalizeDescription(string? description)
    {
        return string.IsNullOrWhiteSpace(description) ? null : description;
    }
}
