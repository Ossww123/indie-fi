using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.Media.Infrastructure;
using IndieFi.Web.Modules.Profiles.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Pages.Games;

// Public: anyone (including logged-out visitors) can view a game's detail and its
// key drops. IsOwner stays false for anonymous users, hiding the owner-only actions.
public class DetailModel : PageModel
{
    public sealed record RequestNotice(
        ReviewRequestOutcome Outcome,
        string Code,
        string Message,
        bool IsSuccess);

    private static readonly RequestNotice[] RequestNotices =
    [
        new(
            ReviewRequestOutcome.Queued,
            "queued",
            "피드백 요청을 등록했습니다. 크레딧 1을 사용했습니다.",
            true),
        new(
            ReviewRequestOutcome.InsufficientCredit,
            "nocredit",
            "크레딧이 부족합니다. 다른 개발자의 게임에 피드백을 주면 크레딧을 벌 수 있습니다.",
            false),
        new(
            ReviewRequestOutcome.Cancelled,
            "cancelled",
            "피드백 요청을 취소했습니다. 크레딧 1을 돌려받았습니다.",
            true),
        new(
            ReviewRequestOutcome.NotCancellable,
            "notcancellable",
            "이미 다른 개발자가 받아 갔거나 취소된 요청입니다.",
            false),
    ];

    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ReviewRequestService _reviewRequestService;
    private readonly UserDisplayNameResolver _displayNames;
    private readonly FavoriteGameService _favoriteGameService;
    private readonly PublicKeyDropReader _publicKeyDropReader;
    private readonly GameClassificationCatalogQueryService _classificationCatalog;
    private readonly MediaObjectStorageOptions _mediaStorageOptions;

    public DetailModel(
        AppDbContext db,
        ICurrentUser currentUser,
        ReviewRequestService reviewRequestService,
        UserDisplayNameResolver displayNames,
        FavoriteGameService favoriteGameService,
        PublicKeyDropReader publicKeyDropReader,
        GameClassificationCatalogQueryService classificationCatalog,
        IOptions<MediaObjectStorageOptions> mediaStorageOptions)
    {
        _db = db;
        _currentUser = currentUser;
        _reviewRequestService = reviewRequestService;
        _displayNames = displayNames;
        _favoriteGameService = favoriteGameService;
        _publicKeyDropReader = publicKeyDropReader;
        _classificationCatalog = classificationCatalog;
        _mediaStorageOptions = mediaStorageOptions.Value;
    }

    public Game Game { get; private set; } = null!;
    public string OwnerDisplayName { get; private set; } = string.Empty;
    public bool IsOwner { get; private set; }
    public bool CanManageMedia => IsOwner && _mediaStorageOptions.Enabled;
    public bool CanManageFavorite { get; private set; }
    public bool IsFavorite { get; private set; }
    public string? FavoriteMessage { get; private set; }
    public List<PlayLinkListItem> PlayLinks { get; private set; } = [];
    public GameClassificationDetailCatalog Classifications { get; private set; } = null!;
    public IReadOnlyList<PublicKeyDropDetail> Drops { get; private set; } = [];
    public int PendingRequestCount { get; private set; }
    public List<RequestListItem> Requests { get; private set; } = [];
    public RequestNotice? RequestMessage { get; private set; }
    public Guid RequestOperationId { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        long id,
        string? request,
        string? favorite)
    {
        // 028 showed that TempData notices can be lost, so the result travels in the URL.
        // Query strings are user-controlled, so only known codes may select displayed text.
        RequestMessage = RequestNotices
            .FirstOrDefault(notice => notice.Code == request);
        FavoriteMessage = favorite switch
        {
            "added" => "관심 게임에 추가했습니다.",
            "removed" => "관심 게임에서 제거했습니다.",
            _ => null,
        };

        var game = await _db.Games.FindAsync(id);

        if (game is null)
        {
            return NotFound();
        }

        // The game detail page is public: any signed-in user may view any game,
        // because feedback added later is left by other people. Ownership is only
        // enforced when editing a game.
        Game = game;
        var classifications = await _classificationCatalog.GetDetailAsync(
            game.Id,
            HttpContext.RequestAborted);
        if (classifications is null)
        {
            return NotFound();
        }

        Classifications = classifications;
        OwnerDisplayName = await _displayNames.ResolveAsync(
            game.OwnerId,
            HttpContext.RequestAborted);
        IsOwner = _currentUser.UserId is long ownerId && game.OwnerId == ownerId;
        CanManageFavorite = _currentUser.UserId is long;
        if (CanManageFavorite)
        {
            IsFavorite = await _favoriteGameService.IsFavoriteAsync(
                _currentUser.UserId!.Value,
                game.Id,
                HttpContext.RequestAborted);
        }
        PlayLinks = await _db.GamePlayLinks
            .Where(link => link.GameId == game.Id)
            .OrderBy(link => link.SortOrder)
            .Select(link => new PlayLinkListItem(
                link.DisplayName,
                link.Provider,
                link.TargetPlatform,
                link.Url))
            .ToListAsync(HttpContext.RequestAborted);

        if (IsOwner)
        {
            RequestOperationId = Guid.NewGuid();
            Requests = await _db.ReviewRequests
                .Where(reviewRequest =>
                    reviewRequest.GameId == game.Id
                    && reviewRequest.Status != ReviewRequestStatus.Cancelled)
                .OrderByDescending(reviewRequest => reviewRequest.Id)
                .Select(reviewRequest => new RequestListItem(
                    reviewRequest.Id,
                    reviewRequest.CreatedAt,
                    reviewRequest.Status,
                    reviewRequest.CompletedAt))
                .ToListAsync();
        }
        else
        {
            PendingRequestCount = await _db.ReviewRequests
                .CountAsync(reviewRequest =>
                    reviewRequest.GameId == game.Id
                    && reviewRequest.Status == ReviewRequestStatus.Queued);
        }

        Drops = await _publicKeyDropReader.ListForGameAsync(
            game.Id,
            HttpContext.RequestAborted);

        return Page();
    }

    public Task<IActionResult> OnPostFavoriteAsync(long id) =>
        ChangeFavoriteAsync(id, favorite: true);

    public Task<IActionResult> OnPostUnfavoriteAsync(long id) =>
        ChangeFavoriteAsync(id, favorite: false);

    public async Task<IActionResult> OnPostRequestFeedbackAsync(
        long id,
        Guid operationId)
    {
        // The page itself stays public, so requesting feedback is guarded here instead.
        // [Authorize] cannot do it: Razor Pages ignores the attribute on a handler.
        if (!_currentUser.IsAuthenticated)
        {
            return Challenge();
        }

        var userId = _currentUser.GetRequiredUserId();
        var result = await _reviewRequestService.RequestAsync(id, userId, operationId);

        if (result.Outcome is ReviewRequestOutcome.InvalidOperationId
            or ReviewRequestOutcome.OperationConflict)
        {
            return BadRequest();
        }

        if (result.Outcome == ReviewRequestOutcome.GameNotFound)
        {
            return NotFound();
        }

        if (result.Outcome == ReviewRequestOutcome.NotOwner)
        {
            return Forbid();
        }

        if (result.Outcome == ReviewRequestOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        var requestNotice = RequestNotices
            .FirstOrDefault(notice => notice.Outcome == result.Outcome);

        if (requestNotice is not null)
        {
            return RedirectToPage(new { id, request = requestNotice.Code });
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelRequestAsync(long id, long requestId)
    {
        // The page itself stays public, so cancellation is guarded here instead.
        // [Authorize] cannot do it: Razor Pages ignores the attribute on a handler.
        if (!_currentUser.IsAuthenticated)
        {
            return Challenge();
        }

        var result = await _reviewRequestService.CancelAsync(
            requestId,
            _currentUser.GetRequiredUserId());
        if (result.Outcome == ReviewRequestOutcome.AccountUnavailable)
        {
            return Challenge();
        }

        var requestNotice = RequestNotices
            .Single(notice => notice.Outcome == result.Outcome);

        return RedirectToPage(new { id, request = requestNotice.Code });
    }

    private async Task<IActionResult> ChangeFavoriteAsync(long id, bool favorite)
    {
        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        var result = favorite
            ? await _favoriteGameService.FavoriteAsync(
                userId,
                id,
                HttpContext.RequestAborted)
            : await _favoriteGameService.UnfavoriteAsync(
                userId,
                id,
                HttpContext.RequestAborted);

        return result.Outcome switch
        {
            FavoriteGameOutcome.Favorited => RedirectToPage(
                new { id, favorite = "added" }),
            FavoriteGameOutcome.NotFavorited => RedirectToPage(
                new { id, favorite = "removed" }),
            FavoriteGameOutcome.GameNotFound => NotFound(),
            FavoriteGameOutcome.AccountUnavailable => Challenge(),
            _ => throw new InvalidOperationException(
                $"Unsupported favorite game outcome: {result.Outcome}"),
        };
    }

    public sealed record RequestListItem(
        long Id,
        DateTimeOffset CreatedAt,
        ReviewRequestStatus Status,
        DateTimeOffset? CompletedAt)
    {
        public bool IsCancellable => Status == ReviewRequestStatus.Queued;

        public string StatusText => Status switch
        {
            ReviewRequestStatus.Queued => "대기 중",
            ReviewRequestStatus.Assigned => "배정됨",
            ReviewRequestStatus.Completed => "완료",
            _ => Status.ToString(),
        };
    }

    public sealed record PlayLinkListItem(
        string DisplayName,
        GamePlayLinkProvider Provider,
        GamePlayLinkPlatform? TargetPlatform,
        string Url)
    {
        public string ProviderText => Provider switch
        {
            GamePlayLinkProvider.Steam => "Steam",
            GamePlayLinkProvider.ItchIo => "itch.io",
            GamePlayLinkProvider.DeveloperWebsite => "개발자 사이트",
            _ => "기타",
        };

        public string? TargetPlatformText => TargetPlatform switch
        {
            GamePlayLinkPlatform.Windows => "Windows",
            GamePlayLinkPlatform.MacOS => "macOS",
            GamePlayLinkPlatform.Linux => "Linux",
            GamePlayLinkPlatform.Web => "Web",
            GamePlayLinkPlatform.Android => "Android",
            GamePlayLinkPlatform.Ios => "iOS",
            GamePlayLinkPlatform.Multiple => "복수 플랫폼",
            GamePlayLinkPlatform.Other => "기타 플랫폼",
            _ => null,
        };
    }
}
