using System.ComponentModel.DataAnnotations;
using System.Net;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public class ResendConfirmationEmailModel : PageModel
{
    private readonly EmailConfirmationResendService _resendService;

    public ResendConfirmationEmailModel(EmailConfirmationResendService resendService)
    {
        _resendService = resendService;
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

        // A missing transport address is rare but must not bypass the IP limit. All
        // such requests share one fail-closed fallback bucket instead.
        var requestIpAddress = HttpContext.Connection.RemoteIpAddress ?? IPAddress.IPv6None;
        var result = await _resendService.RequestAsync(
            Email,
            requestIpAddress,
            HttpContext.RequestAborted);

        if (result.Outcome == EmailConfirmationResendOutcome.InvalidEmail)
        {
            ModelState.AddModelError(
                EmailConfirmationResendService.EmailField,
                result.EmailError!);
            return Page();
        }

        // Do not reveal whether the account exists, is confirmed, or hit an account/IP
        // limit. The same PRG response is used for every syntactically valid address.
        RequestCompleted = true;
        return RedirectToPage("/ResendConfirmationEmail");
    }

    private void SetPrivateResponseHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
