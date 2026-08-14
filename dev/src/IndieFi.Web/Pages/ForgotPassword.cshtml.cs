using System.ComponentModel.DataAnnotations;
using System.Net;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public class ForgotPasswordModel : PageModel
{
    private readonly PasswordResetRequestService _passwordResetRequests;

    public ForgotPasswordModel(PasswordResetRequestService passwordResetRequests)
    {
        _passwordResetRequests = passwordResetRequests;
    }

    [BindProperty]
    [Required(ErrorMessage = "이메일을 입력해 주세요.")]
    [DataType(DataType.EmailAddress)]
    public string Email { get; set; } = string.Empty;

    [TempData]
    public bool RequestCompleted { get; set; }

    public void OnGet()
    {
        SetPrivateResponseHeaders();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        SetPrivateResponseHeaders();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Missing transport addresses share one fail-closed bucket rather than
        // bypassing the IP limit.
        var requestIpAddress = HttpContext.Connection.RemoteIpAddress ?? IPAddress.IPv6None;
        var result = await _passwordResetRequests.RequestAsync(
            Email,
            requestIpAddress,
            HttpContext.RequestAborted);

        if (result.Outcome == PasswordResetRequestOutcome.InvalidEmail)
        {
            ModelState.AddModelError(
                PasswordResetRequestService.EmailField,
                result.EmailError!);
            return Page();
        }

        // Only this boolean enters encrypted TempData. The email and internal account
        // state never enter the redirect URL or the result page.
        RequestCompleted = true;
        return RedirectToPage("/ForgotPassword");
    }

    private void SetPrivateResponseHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
