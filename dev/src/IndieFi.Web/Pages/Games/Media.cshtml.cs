using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Pages.Games;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MediaModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly IServiceProvider _services;
    private readonly MediaObjectStorageOptions _storageOptions;

    public MediaModel(
        ICurrentUser currentUser,
        IServiceProvider services,
        IOptions<MediaObjectStorageOptions> storageOptions)
    {
        _currentUser = currentUser;
        _services = services;
        _storageOptions = storageOptions.Value;
    }

    public OwnerGameMedia Media { get; private set; } = null!;

    public int MaximumScreenshotCount =>
        MediaPolicy.GameScreenshotMaximumCount;

    public long MaximumFileBytes => MediaPolicy.GameImageMaximumBytes;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (id <= 0 || !_storageOptions.Enabled)
        {
            return NotFound();
        }

        Response.Headers["Referrer-Policy"] = "no-referrer";

        var media = await _services
            .GetRequiredService<GameMediaQueryService>()
            .GetOwnerAsync(
                _currentUser.GetRequiredUserId(),
                id,
                HttpContext.RequestAborted);
        if (media is null)
        {
            // Missing and foreign games share one response so this page does not
            // reveal another user's game ownership boundary.
            return NotFound();
        }

        Media = media;
        return Page();
    }

    public static string StatusText(MediaStatus status) => status switch
    {
        MediaStatus.UploadPending => "업로드 대기",
        MediaStatus.ProcessingPending => "처리 대기",
        MediaStatus.Processing => "처리 중",
        MediaStatus.Ready => "공개 중",
        MediaStatus.Failed => "처리 실패",
        MediaStatus.Expired => "업로드 만료",
        _ => "사용할 수 없음",
    };

    public static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.#} MiB"
            : $"{bytes / 1024d:0.#} KiB";

    public static bool CanRemove(MediaStatus status) =>
        MediaLifecyclePolicy.CanTransition(status, MediaStatus.DeletePending);
}
