using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public class LoginModel : PageModel
{
    private readonly AccountAuthenticationService _accountAuthenticationService;

    public LoginModel(AccountAuthenticationService accountAuthenticationService)
    {
        _accountAuthenticationService = accountAuthenticationService;
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

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.UserId!.Value.ToString()),
            new(ClaimTypes.Name, UserDisplayIdentifier.Format(result.UserId.Value)),
            new(
                AuthenticationClaimTypes.SessionVersion,
                result.SessionVersion!.Value.ToString()),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        // ReturnUrl is attacker-controlled, so only local URLs may become post-login redirects.
        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
    }
}
