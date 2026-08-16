using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Games.Domain;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

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
    private readonly GamePlayLinkManagementService _gamePlayLinkManagementService;
    private readonly GameClassificationSelectionService _classificationSelectionService;
    private readonly ISocialNotificationFanoutPublisher _notificationFanout;

    public GameManagementService(
        AppDbContext db,
        ISocialNotificationFanoutPublisher notificationFanout)
    {
        _db = db;
        _gamePlayLinkManagementService = new GamePlayLinkManagementService(db);
        _classificationSelectionService = new GameClassificationSelectionService(db);
        _notificationFanout = notificationFanout;
    }

    public Task<CreateGameResult> CreateAsync(
        long ownerId,
        string? title,
        string? description)
    {
        return CreateAsync(ownerId, title, description, [], CancellationToken.None);
    }

    public async Task<CreateGameResult> CreateAsync(
        long ownerId,
        string? title,
        string? description,
        IReadOnlyList<GamePlayLinkInput> links,
        CancellationToken cancellationToken)
    {
        return await CreateCoreAsync(
            ownerId,
            title,
            description,
            classification: null,
            links,
            cancellationToken);
    }

    public Task<CreateGameResult> CreateAsync(
        long ownerId,
        string? title,
        string? description,
        GameClassificationInput classification,
        IReadOnlyList<GamePlayLinkInput> links,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return CreateCoreAsync(
            ownerId,
            title,
            description,
            classification,
            links,
            cancellationToken);
    }

    private async Task<CreateGameResult> CreateCoreAsync(
        long ownerId,
        string? title,
        string? description,
        GameClassificationInput? classification,
        IReadOnlyList<GamePlayLinkInput> links,
        CancellationToken cancellationToken)
    {
        var violations = Validate(title, description);
        var linkValidation = GamePlayLinkManagementService.Validate(links);
        violations.AddRange(linkValidation.Violations);
        GameClassificationValidationResult? classificationValidation = null;
        if (classification is not null)
        {
            classificationValidation = await _classificationSelectionService
                .ValidateForCreateAsync(classification, cancellationToken);
            violations.AddRange(classificationValidation.Violations);
        }

        if (violations.Count > 0)
        {
            return new CreateGameResult(CreateGameOutcome.Invalid, violations);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db, cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(_db, ownerId, cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new CreateGameResult(
                CreateGameOutcome.AccountUnavailable,
                NoViolations);
        }

        var game = new Game
        {
            OwnerId = ownerId,
            GenreId = classificationValidation?.Selection?.GenreId
                ?? GameClassificationCatalog.UnspecifiedGenreId,
            Title = title!.Trim(),
            Description = NormalizeDescription(description),
        };

        _db.Games.Add(game);
        await _db.SaveChangesAsync(cancellationToken);
        if (classificationValidation?.Selection is { } selection)
        {
            AddClassificationAssignments(game.Id, selection);
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (linkValidation.Links.Count > 0)
        {
            await _gamePlayLinkManagementService.ReplaceValidatedAsync(
                game.Id,
                linkValidation.Links,
                cancellationToken);
        }

        await _notificationFanout.StageGamePublishedAsync(
            NotificationEventId.Create(
                NotificationKind.FollowedUserGamePublished,
                game.Id,
                ownerId),
            game.Id,
            game.CreatedAt,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);

        return new CreateGameResult(
            CreateGameOutcome.Created,
            NoViolations,
            game.Id,
            game.Title);
    }

    public Task<UpdateGameResult> UpdateAsync(
        long gameId,
        long ownerId,
        string? title,
        string? description)
    {
        return UpdateAsync(
            gameId,
            ownerId,
            title,
            description,
            links: null,
            cancellationToken: CancellationToken.None);
    }

    public async Task<UpdateGameResult> UpdateAsync(
        long gameId,
        long ownerId,
        string? title,
        string? description,
        IReadOnlyList<GamePlayLinkInput>? links,
        CancellationToken cancellationToken)
    {
        return await UpdateCoreAsync(
            gameId,
            ownerId,
            title,
            description,
            classification: null,
            links,
            cancellationToken);
    }

    public Task<UpdateGameResult> UpdateAsync(
        long gameId,
        long ownerId,
        string? title,
        string? description,
        GameClassificationInput classification,
        IReadOnlyList<GamePlayLinkInput>? links,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return UpdateCoreAsync(
            gameId,
            ownerId,
            title,
            description,
            classification,
            links,
            cancellationToken);
    }

    private async Task<UpdateGameResult> UpdateCoreAsync(
        long gameId,
        long ownerId,
        string? title,
        string? description,
        GameClassificationInput? classification,
        IReadOnlyList<GamePlayLinkInput>? links,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db, cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(_db, ownerId, cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UpdateGameResult(
                UpdateGameOutcome.AccountUnavailable,
                NoViolations);
        }

        var game = await _db.Games.FindAsync([gameId], cancellationToken);

        // Checked before validation so an unauthorized caller cannot tell a missing
        // game apart from one they do not own by comparing error messages.
        if (game is null || game.OwnerId != ownerId)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UpdateGameResult(UpdateGameOutcome.NotFound, NoViolations);
        }

        var violations = Validate(title, description);
        var linkValidation = links is null
            ? null
            : GamePlayLinkManagementService.Validate(links);
        if (linkValidation is not null)
        {
            violations.AddRange(linkValidation.Violations);
        }

        GameClassificationValidationResult? classificationValidation = null;
        if (classification is not null)
        {
            var current = new CurrentGameClassification(
                game.GenreId,
                await _db.GameTagAssignments
                    .Where(item => item.GameId == gameId)
                    .Select(item => item.TagId)
                    .ToListAsync(cancellationToken),
                await _db.GamePlatformAssignments
                    .Where(item => item.GameId == gameId)
                    .Select(item => item.PlatformId)
                    .ToListAsync(cancellationToken));
            classificationValidation = await _classificationSelectionService
                .ValidateForUpdateAsync(classification, current, cancellationToken);
            violations.AddRange(classificationValidation.Violations);
        }

        if (violations.Count > 0)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UpdateGameResult(UpdateGameOutcome.Invalid, violations);
        }

        game.Title = title!.Trim();
        game.Description = NormalizeDescription(description);
        if (classificationValidation?.Selection is { } selection)
        {
            game.GenreId = selection.GenreId;
            await ReplaceClassificationAssignmentsAsync(
                gameId,
                selection,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        if (linkValidation is not null)
        {
            await _gamePlayLinkManagementService.ReplaceValidatedAsync(
                gameId,
                linkValidation.Links,
                cancellationToken);
        }

        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);

        return new UpdateGameResult(UpdateGameOutcome.Updated, NoViolations);
    }

    private void AddClassificationAssignments(
        long gameId,
        ValidatedGameClassification selection)
    {
        _db.GameTagAssignments.AddRange(selection.TagIds.Select(tagId =>
            new GameTagAssignment
            {
                GameId = gameId,
                TagId = tagId,
            }));
        _db.GamePlatformAssignments.AddRange(selection.PlatformIds.Select(platformId =>
            new GamePlatformAssignment
            {
                GameId = gameId,
                PlatformId = platformId,
            }));
    }

    private async Task ReplaceClassificationAssignmentsAsync(
        long gameId,
        ValidatedGameClassification selection,
        CancellationToken cancellationToken)
    {
        await _db.GameTagAssignments
            .Where(item => item.GameId == gameId)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.GamePlatformAssignments
            .Where(item => item.GameId == gameId)
            .ExecuteDeleteAsync(cancellationToken);
        AddClassificationAssignments(gameId, selection);
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
