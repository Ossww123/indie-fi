using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class StatusModel : PageModel
{
    public int DisplayStatusCode { get; private set; }

    public string Title => DisplayStatusCode switch
    {
        StatusCodes.Status400BadRequest => "요청을 확인해 주세요.",
        StatusCodes.Status401Unauthorized => "로그인이 필요합니다.",
        StatusCodes.Status403Forbidden => "이 작업을 수행할 수 없습니다.",
        StatusCodes.Status404NotFound => "페이지를 찾을 수 없습니다.",
        StatusCodes.Status405MethodNotAllowed => "지원하지 않는 요청입니다.",
        StatusCodes.Status429TooManyRequests => "요청이 너무 많습니다.",
        StatusCodes.Status503ServiceUnavailable => "잠시 사용할 수 없습니다.",
        >= StatusCodes.Status500InternalServerError => "잠시 문제가 생겼습니다.",
        _ => "요청을 처리할 수 없습니다.",
    };

    public string Description => DisplayStatusCode switch
    {
        StatusCodes.Status400BadRequest => "입력이나 주소를 확인한 뒤 다시 시도해 주세요.",
        StatusCodes.Status401Unauthorized => "계속하려면 로그인한 뒤 다시 시도해 주세요.",
        StatusCodes.Status403Forbidden => "현재 계정으로는 이 요청을 완료할 수 없습니다.",
        StatusCodes.Status404NotFound => "주소가 바뀌었거나 접근할 수 없는 항목일 수 있습니다.",
        StatusCodes.Status405MethodNotAllowed => "올바른 화면에서 작업을 다시 시작해 주세요.",
        StatusCodes.Status429TooManyRequests => "잠시 기다린 뒤 다시 시도해 주세요.",
        StatusCodes.Status503ServiceUnavailable => "서비스가 복구될 때까지 잠시 기다려 주세요.",
        >= StatusCodes.Status500InternalServerError => "일시적인 문제일 수 있으니 잠시 뒤 다시 시도해 주세요.",
        _ => "요청을 다시 확인하거나 홈으로 돌아가 주세요.",
    };

    public string ReturnUrl { get; private set; } = "/";

    public void OnGet(int code)
    {
        DisplayStatusCode = code is >= 400 and <= 599
            ? code
            : StatusCodes.Status404NotFound;
        Response.StatusCode = DisplayStatusCode;

        var originalPath = HttpContext.Features
            .Get<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>()
            ?.OriginalPath;
        if (!string.IsNullOrEmpty(originalPath) && originalPath.StartsWith('/'))
        {
            ReturnUrl = originalPath;
        }
    }
}
