using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public sealed class GoogleLoginModel : PageModel
{
    private const string GenericFailureMessage =
        "Google 로그인을 완료하지 못했습니다. 다시 시도해 주세요.";
    private const string LinkUnavailableMessage =
        "이 이메일의 기존 계정에는 새 Google 로그인을 연결할 수 없습니다. " +
        "기존 로그인 수단을 이용해 주세요.";

    private readonly GoogleAccountService _googleAccounts;
    private readonly AccountSignInService _accountSignIn;

    public GoogleLoginModel(
        GoogleAccountService googleAccounts,
        AccountSignInService accountSignIn)
    {
        _googleAccounts = googleAccounts;
        _accountSignIn = accountSignIn;
    }

    [TempData]
    public string? GoogleLoginError { get; set; }

    public IActionResult OnGet()
    {
        SetPrivateResponseHeaders();
        return RedirectToPage("/Login");
    }

    public async Task<IActionResult> OnPostChallengeAsync(string? returnUrl)
    {
        SetPrivateResponseHeaders();
        await HttpContext.SignOutAsync(ExternalAuthenticationDefaults.TemporaryScheme);

        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        var callbackUrl = Url.Page(
            "/GoogleLogin",
            "Callback",
            new { returnUrl = safeReturnUrl });
        if (string.IsNullOrEmpty(callbackUrl))
        {
            throw new InvalidOperationException("The Google callback URL could not be created.");
        }

        return Challenge(
            new AuthenticationProperties { RedirectUri = callbackUrl },
            ExternalLoginPolicy.GoogleProvider);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl)
    {
        SetPrivateResponseHeaders();
        var externalAuthentication = await HttpContext.AuthenticateAsync(
            ExternalAuthenticationDefaults.TemporaryScheme);

        if (!externalAuthentication.Succeeded
            || !GoogleExternalIdentityClaims.TryRead(
                externalAuthentication.Principal,
                out var subject,
                out var email))
        {
            return await FailAsync(returnUrl);
        }

        var result = await _googleAccounts.ResolveAsync(
            subject!,
            email!,
            HttpContext.RequestAborted);
        if (result.Outcome == GoogleAccountOutcome.RequiresPasswordLink)
        {
            var safeReturnUrl = GetSafeReturnUrl(returnUrl);
            return safeReturnUrl == "/"
                ? RedirectToPage("/GoogleLink")
                : RedirectToPage("/GoogleLink", new { returnUrl = safeReturnUrl });
        }

        if (result.Outcome == GoogleAccountOutcome.LinkUnavailable)
        {
            return await FailAsync(returnUrl, LinkUnavailableMessage);
        }

        await HttpContext.SignOutAsync(ExternalAuthenticationDefaults.TemporaryScheme);
        await _accountSignIn.SignInAsync(
            HttpContext,
            result.UserId!.Value,
            result.SessionVersion!.Value);

        return LocalRedirect(GetSafeReturnUrl(returnUrl));
    }

    private async Task<IActionResult> FailAsync(
        string? returnUrl,
        string message = GenericFailureMessage)
    {
        await HttpContext.SignOutAsync(ExternalAuthenticationDefaults.TemporaryScheme);
        GoogleLoginError = message;
        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        return safeReturnUrl == "/"
            ? RedirectToPage("/Login")
            : RedirectToPage("/Login", new { returnUrl = safeReturnUrl });
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
