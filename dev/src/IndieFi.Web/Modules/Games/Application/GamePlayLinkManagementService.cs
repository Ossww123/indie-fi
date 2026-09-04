using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Games.Application;

public enum UpdateGamePlayLinksOutcome
{
    Updated,
    NotFound,
    Invalid,
    AccountUnavailable,
}

public sealed record GamePlayLinkInput(
    string? DisplayName,
    GamePlayLinkProvider Provider,
    GamePlayLinkPlatform? TargetPlatform,
    string? Url);

public sealed record UpdateGamePlayLinksResult(
    UpdateGamePlayLinksOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations);

public sealed class GamePlayLinkManagementService
{
    public const string LinksField = "Links";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;

    public GamePlayLinkManagementService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Replaces the ordered link collection in one transaction. Submitting the
    /// complete collection lets the same operation add, edit, remove, and reorder
    /// links without exposing partially updated state.
    /// </summary>
    public async Task<UpdateGamePlayLinksResult> UpdateAsync(
        long gameId,
        long ownerId,
        IReadOnlyList<GamePlayLinkInput>? links,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(_db, cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(_db, ownerId, cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UpdateGamePlayLinksResult(
                UpdateGamePlayLinksOutcome.AccountUnavailable,
                NoViolations);
        }

        var game = await _db.Games
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == gameId, cancellationToken);

        // Keep missing and someone else's games indistinguishable to callers.
        if (game is null || game.OwnerId != ownerId)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UpdateGamePlayLinksResult(
                UpdateGamePlayLinksOutcome.NotFound,
                NoViolations);
        }

        var candidates = links ?? [];
        var validation = Validate(candidates);
        if (validation.Violations.Count > 0)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);
            return new UpdateGamePlayLinksResult(
                UpdateGamePlayLinksOutcome.Invalid,
                validation.Violations);
        }

        await ReplaceValidatedAsync(gameId, validation.Links, cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);

        return new UpdateGamePlayLinksResult(
            UpdateGamePlayLinksOutcome.Updated,
            NoViolations);
    }

    public static string DisplayNameField(int index) => $"Links[{index}].DisplayName";

    public static string ProviderField(int index) => $"Links[{index}].Provider";

    public static string TargetPlatformField(int index) => $"Links[{index}].TargetPlatform";

    public static string UrlField(int index) => $"Links[{index}].Url";

    internal async Task ReplaceValidatedAsync(
        long gameId,
        IReadOnlyList<NormalizedGamePlayLink> links,
        CancellationToken cancellationToken = default)
    {
        // Delete and insert are intentionally separate database commands. The
        // caller's transaction makes room for reused sort positions while also
        // restoring the original collection if an insert fails.
        await _db.GamePlayLinks
            .Where(link => link.GameId == gameId)
            .ExecuteDeleteAsync(cancellationToken);

        _db.GamePlayLinks.AddRange(links.Select((link, sortOrder) =>
            new GamePlayLink
            {
                GameId = gameId,
                DisplayName = link.DisplayName,
                Provider = link.Provider,
                TargetPlatform = link.TargetPlatform,
                Url = link.Url,
                SortOrder = sortOrder,
            }));
        await _db.SaveChangesAsync(cancellationToken);
    }

    internal static LinkValidationResult Validate(IReadOnlyList<GamePlayLinkInput> links)
    {
        var violations = new List<ValidationViolation>();
        var normalizedLinks = new List<NormalizedGamePlayLink>(links.Count);

        if (links.Count > GamePlayLinkPolicy.MaxLinksPerGame)
        {
            violations.Add(new ValidationViolation(
                LinksField,
                $"외부 플레이 링크는 최대 {GamePlayLinkPolicy.MaxLinksPerGame}개까지 등록할 수 있습니다."));
        }

        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var itemIsValid = true;

            if (!GamePlayLinkPolicy.TryNormalizeDisplayName(
                    link.DisplayName,
                    out var displayName,
                    out var displayNameError))
            {
                violations.Add(new ValidationViolation(
                    DisplayNameField(index),
                    DisplayNameErrorMessage(displayNameError)));
                itemIsValid = false;
            }

            if (!Enum.IsDefined(link.Provider))
            {
                violations.Add(new ValidationViolation(
                    ProviderField(index),
                    "올바른 링크 제공처를 선택해 주세요."));
                itemIsValid = false;
            }

            if (link.TargetPlatform is { } targetPlatform
                && !Enum.IsDefined(targetPlatform))
            {
                violations.Add(new ValidationViolation(
                    TargetPlatformField(index),
                    "올바른 대상 플랫폼을 선택해 주세요."));
                itemIsValid = false;
            }

            if (!GamePlayLinkPolicy.TryCreateAddress(
                    link.Url,
                    out var address,
                    out var addressError))
            {
                violations.Add(new ValidationViolation(
                    UrlField(index),
                    AddressErrorMessage(addressError)));
                itemIsValid = false;
            }

            if (itemIsValid)
            {
                normalizedLinks.Add(new NormalizedGamePlayLink(
                    displayName!,
                    link.Provider,
                    link.TargetPlatform,
                    address!.Value));
            }
        }

        return new LinkValidationResult(normalizedLinks, violations);
    }

    private static string DisplayNameErrorMessage(GamePlayLinkDisplayNameError error) =>
        error switch
        {
            GamePlayLinkDisplayNameError.Required => "링크 표시 이름을 입력해 주세요.",
            GamePlayLinkDisplayNameError.TooLong =>
                $"링크 표시 이름은 {GamePlayLinkPolicy.DisplayNameMaxLength}자 이하여야 합니다.",
            _ => "올바른 링크 표시 이름을 입력해 주세요.",
        };

    private static string AddressErrorMessage(GamePlayLinkAddressError error) =>
        error switch
        {
            GamePlayLinkAddressError.Required => "외부 플레이 주소를 입력해 주세요.",
            GamePlayLinkAddressError.TooLong =>
                $"외부 플레이 주소는 {GamePlayLinkPolicy.UrlMaxLength}자 이하여야 합니다.",
            GamePlayLinkAddressError.UnsupportedScheme =>
                "외부 플레이 주소는 HTTP 또는 HTTPS 주소여야 합니다.",
            GamePlayLinkAddressError.UserInfoNotAllowed =>
                "사용자 정보가 포함된 외부 플레이 주소는 사용할 수 없습니다.",
            GamePlayLinkAddressError.NonPublicHost =>
                "공개 인터넷 호스트의 외부 플레이 주소를 입력해 주세요.",
            _ => "올바른 외부 플레이 주소를 입력해 주세요.",
        };

    internal sealed record NormalizedGamePlayLink(
        string DisplayName,
        GamePlayLinkProvider Provider,
        GamePlayLinkPlatform? TargetPlatform,
        string Url);

    internal sealed record LinkValidationResult(
        IReadOnlyList<NormalizedGamePlayLink> Links,
        IReadOnlyList<ValidationViolation> Violations);
}
