using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public sealed class GoogleLinkModel : PageModel
{
    private const string GenericFailureMessage =
        "Google 계정 연결을 완료하지 못했습니다. Google 로그인을 다시 시작해 주세요.";

    private readonly GoogleAccountService _googleAccounts;
    private readonly AccountSignInService _accountSignIn;

    public GoogleLinkModel(
        GoogleAccountService googleAccounts,
        AccountSignInService accountSignIn)
    {
        _googleAccounts = googleAccounts;
        _accountSignIn = accountSignIn;
    }

    [BindProperty]
    [Required(ErrorMessage = "현재 비밀번호를 입력해 주세요.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [TempData]
    public string? GoogleLoginError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        SetPrivateResponseHeaders();
        ReturnUrl = GetSafeReturnUrl(ReturnUrl);
        var identity = await ReadExternalIdentityAsync();
        if (identity is null)
        {
            return await FailAsync();
        }

        var availability = await _googleAccounts.GetLinkAvailabilityAsync(
            identity.Value.Subject,
            identity.Value.Email,
            HttpContext.RequestAborted);
        return availability == GoogleAccountOutcome.RequiresPasswordLink
            ? Page()
            : await FailAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetPrivateResponseHeaders();
        ReturnUrl = GetSafeReturnUrl(ReturnUrl);
        var identity = await ReadExternalIdentityAsync();
        if (identity is null)
        {
            return await FailAsync();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _googleAccounts.LinkAsync(
            identity.Value.Subject,
            identity.Value.Email,
            Password,
            HttpContext.RequestAborted);
        if (result.Outcome == GoogleAccountLinkOutcome.InvalidPassword)
        {
            ModelState.AddModelError(
                nameof(Password),
                "현재 비밀번호가 올바르지 않습니다.");
            return Page();
        }

        if (result.Outcome != GoogleAccountLinkOutcome.Linked)
        {
            return await FailAsync();
        }

        await HttpContext.SignOutAsync(ExternalAuthenticationDefaults.TemporaryScheme);
        await _accountSignIn.SignInAsync(
            HttpContext,
            result.UserId!.Value,
            result.SessionVersion!.Value);
        return LocalRedirect(ReturnUrl!);
    }

    private async Task<(string Subject, AccountEmailAddress Email)?>
        ReadExternalIdentityAsync()
    {
        var authentication = await HttpContext.AuthenticateAsync(
            ExternalAuthenticationDefaults.TemporaryScheme);
        return authentication.Succeeded
            && GoogleExternalIdentityClaims.TryRead(
                authentication.Principal,
                out var subject,
                out var email)
            ? (subject!, email!)
            : null;
    }

    private async Task<IActionResult> FailAsync()
    {
        await HttpContext.SignOutAsync(ExternalAuthenticationDefaults.TemporaryScheme);
        GoogleLoginError = GenericFailureMessage;
        return ReturnUrl == "/"
            ? RedirectToPage("/Login")
            : RedirectToPage("/Login", new { returnUrl = ReturnUrl });
    }

    private string GetSafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : "/";

    private void SetPrivateResponseHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
