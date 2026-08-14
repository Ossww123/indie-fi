using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public enum EmailConfirmationPageState
{
    Instructions,
    Confirmed,
    Invalid,
}

public class ConfirmEmailModel : PageModel
{
    private readonly EmailConfirmationTokenService _emailConfirmationTokens;

    public ConfirmEmailModel(EmailConfirmationTokenService emailConfirmationTokens)
    {
        _emailConfirmationTokens = emailConfirmationTokens;
    }

    [TempData]
    public bool? ConfirmationSucceeded { get; set; }

    public EmailConfirmationPageState State { get; private set; } =
        EmailConfirmationPageState.Instructions;

    public async Task<IActionResult> OnGetAsync(string? token)
    {
        // Confirmation tokens must not remain in browser history, caches, or referrers.
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        if (ConfirmationSucceeded is bool succeeded)
        {
            State = succeeded
                ? EmailConfirmationPageState.Confirmed
                : EmailConfirmationPageState.Invalid;
            return Page();
        }

        if (string.IsNullOrEmpty(token))
        {
            return Page();
        }

        var result = await _emailConfirmationTokens.ConsumeAsync(
            token,
            HttpContext.RequestAborted);
        ConfirmationSucceeded =
            result.Outcome == EmailConfirmationTokenConsumeOutcome.Confirmed;

        // The result is kept as a boolean in encrypted TempData; the token itself is
        // removed from the address bar before any result page is rendered.
        return RedirectToPage("/ConfirmEmail");
    }
}
