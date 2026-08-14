using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public enum EmailConfirmationPageState
{
    Instructions,
    ReadyToConfirm,
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

    [BindProperty]
    public string? Token { get; set; }

    [TempData]
    public bool? ConfirmationSucceeded { get; set; }

    public EmailConfirmationPageState State { get; private set; } =
        EmailConfirmationPageState.Instructions;

    /// <summary>
    /// Opening the link only offers the button. Mail scanners and browser prefetchers
    /// issue this GET without a person involved, so it must not spend the token.
    /// </summary>
    public IActionResult OnGet(string? token)
    {
        SetPrivateResponseHeaders();

        if (ConfirmationSucceeded is bool succeeded)
        {
            State = succeeded
                ? EmailConfirmationPageState.Confirmed
                : EmailConfirmationPageState.Invalid;
            return Page();
        }

        if (!string.IsNullOrEmpty(token))
        {
            Token = token;
            State = EmailConfirmationPageState.ReadyToConfirm;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetPrivateResponseHeaders();

        var result = await _emailConfirmationTokens.ConsumeAsync(
            Token,
            HttpContext.RequestAborted);
        ConfirmationSucceeded =
            result.Outcome == EmailConfirmationTokenConsumeOutcome.Confirmed;

        // The result is kept as a boolean in encrypted TempData; the token itself is
        // removed from the address bar before any result page is rendered.
        return RedirectToPage("/ConfirmEmail");
    }

    private void SetPrivateResponseHeaders()
    {
        // Confirmation tokens must not remain in browser history, caches, or referrers.
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
