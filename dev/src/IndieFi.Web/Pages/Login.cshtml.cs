using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Pages;

public class LoginModel : PageModel
{
    private readonly AccountAuthenticationService _accountAuthenticationService;
    private readonly AccountSignInService _accountSignInService;

    public LoginModel(
        AccountAuthenticationService accountAuthenticationService,
        AccountSignInService accountSignInService,
        IOptions<GoogleAuthenticationSettings> googleAuthentication)
    {
        _accountAuthenticationService = accountAuthenticationService;
        _accountSignInService = accountSignInService;
        GoogleLoginAvailable = googleAuthentication.Value.IsConfigured;
    }

    [BindProperty]
    [Required(ErrorMessage = "이메일을 입력해 주세요.")]
    [DataType(DataType.EmailAddress)]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "비밀번호를 입력해 주세요.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? GoogleLoginError { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? GoogleError { get; set; }

    public string? GoogleErrorMessage => GoogleLoginError
        ?? (string.Equals(GoogleError, "remote", StringComparison.Ordinal)
            ? "Google 인증을 완료하지 못했습니다. 이메일 로그인은 계속 사용할 수 있습니다."
            : null);

    public bool GoogleLoginAvailable { get; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _accountAuthenticationService.AuthenticateAsync(Email, Password);

        if (result.Outcome == AccountAuthenticationOutcome.EmailNotConfirmed)
        {
            ModelState.AddModelError(
                string.Empty,
                "이메일 확인이 필요합니다. 가입할 때 받은 확인 메일을 확인해 주세요.");
            return Page();
        }

        if (result.Outcome == AccountAuthenticationOutcome.InvalidCredentials)
        {
            // Deliberately the same message either way: saying "no such account"
            // would let anyone probe which email addresses are registered.
            ModelState.AddModelError(string.Empty, "이메일 또는 비밀번호가 올바르지 않습니다.");
            return Page();
        }

        if (result.Outcome == AccountAuthenticationOutcome.AccountUnavailable)
        {
            ModelState.AddModelError(
                string.Empty,
                "현재 이 계정으로 로그인할 수 없습니다.");
            return Page();
        }

        await _accountSignInService.SignInAsync(
            HttpContext,
            result.UserId!.Value,
            result.SessionVersion!.Value,
            HttpContext.RequestAborted);

        // ReturnUrl is attacker-controlled, so only local URLs may become post-login redirects.
        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
    }
}
