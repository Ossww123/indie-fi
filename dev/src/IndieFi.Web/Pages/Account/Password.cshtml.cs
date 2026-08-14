using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Account;

[Authorize]
public sealed class PasswordModel : PageModel
{
    private readonly ICurrentUser _currentUser;
    private readonly AccountPasswordChangeService _passwordChangeService;

    public PasswordModel(
        ICurrentUser currentUser,
        AccountPasswordChangeService passwordChangeService)
    {
        _currentUser = currentUser;
        _passwordChangeService = passwordChangeService;
    }

    [BindProperty]
    [DataType(DataType.Password)]
    public string? CurrentPassword { get; set; }

    [BindProperty]
    [DataType(DataType.Password)]
    public string? NewPassword { get; set; }

    [BindProperty]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "새 비밀번호가 서로 일치하지 않습니다.")]
    public string? ConfirmNewPassword { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (_currentUser.UserId is not long userId)
        {
            return Challenge();
        }

        var result = await _passwordChangeService.ChangeAsync(
            userId,
            CurrentPassword,
            NewPassword,
            HttpContext.RequestAborted);

        if (result.Outcome == AccountPasswordChangeOutcome.AccountUnavailable)
        {
            ModelState.AddModelError(
                string.Empty,
                "이 계정에서는 비밀번호를 변경할 수 없습니다.");
            return Page();
        }

        if (result.Outcome != AccountPasswordChangeOutcome.Changed)
        {
            foreach (var violation in result.Violations)
            {
                ModelState.AddModelError(violation.Field, violation.Message);
            }

            return Page();
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData[nameof(LoginModel.StatusMessage)] =
            "비밀번호가 변경되었습니다. 새 비밀번호로 다시 로그인해 주세요.";
        return RedirectToPage("/Login");
    }
}
