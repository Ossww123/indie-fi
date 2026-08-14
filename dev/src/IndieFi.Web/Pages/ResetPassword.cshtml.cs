using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public enum PasswordResetPageState
{
    Instructions,
    ReadyToReset,
    ResetCompleted,
    Invalid,
}

public sealed class ResetPasswordModel : PageModel
{
    private readonly PasswordResetCompletionService _passwordResetCompletion;

    public ResetPasswordModel(PasswordResetCompletionService passwordResetCompletion)
    {
        _passwordResetCompletion = passwordResetCompletion;
    }

    [BindProperty]
    public string? Token { get; set; }

    [BindProperty]
    [DataType(DataType.Password)]
    public string? NewPassword { get; set; }

    [BindProperty]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "새 비밀번호가 서로 일치하지 않습니다.")]
    public string? ConfirmNewPassword { get; set; }

    [TempData]
    public bool? ResetSucceeded { get; set; }

    public PasswordResetPageState State { get; private set; } =
        PasswordResetPageState.Instructions;

    /// <summary>
    /// Mail scanners and link prefetchers open GET links without user intent. Opening
    /// this page therefore only offers the form and must never consume the token.
    /// </summary>
    public IActionResult OnGet(string? token)
    {
        SetPrivateResponseHeaders();

        if (ResetSucceeded is bool succeeded)
        {
            State = succeeded
                ? PasswordResetPageState.ResetCompleted
                : PasswordResetPageState.Invalid;
            return Page();
        }

        if (!string.IsNullOrEmpty(token))
        {
            Token = token;
            State = PasswordResetPageState.ReadyToReset;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetPrivateResponseHeaders();

        if (!ModelState.IsValid)
        {
            State = PasswordResetPageState.ReadyToReset;
            return Page();
        }

        var result = await _passwordResetCompletion.CompleteAsync(
            Token,
            NewPassword,
            HttpContext.RequestAborted);

        if (result.Outcome == PasswordResetCompletionOutcome.InvalidNewPassword)
        {
            foreach (var violation in result.Violations)
            {
                ModelState.AddModelError(violation.Field, violation.Message);
            }

            State = PasswordResetPageState.ReadyToReset;
            return Page();
        }

        ResetSucceeded = result.Outcome == PasswordResetCompletionOutcome.Completed;
        if (ResetSucceeded == true)
        {
            await HttpContext.SignOutAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);
        }

        // Encrypted TempData keeps only the boolean result. The raw token is removed
        // from the result address and is never copied into a status message.
        return RedirectToPage("/ResetPassword");
    }

    private void SetPrivateResponseHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
