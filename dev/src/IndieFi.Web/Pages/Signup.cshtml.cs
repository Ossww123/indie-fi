using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages;

public class SignupModel : PageModel
{
    private readonly AccountRegistrationService _accountRegistrationService;

    public SignupModel(AccountRegistrationService accountRegistrationService)
    {
        _accountRegistrationService = accountRegistrationService;
    }

    // The service owns validation. [DataType] only controls how the browser renders
    // each field, so it does not duplicate any validation rule.
    [BindProperty]
    [DataType(DataType.EmailAddress)]
    public string? Email { get; set; }

    [BindProperty]
    [DataType(DataType.Password)]
    public string? Password { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await _accountRegistrationService.RegisterAsync(Email, Password);

        if (result.Outcome == AccountRegistrationOutcome.EmailAlreadyRegistered)
        {
            ModelState.AddModelError(string.Empty, "이미 가입된 이메일입니다.");
            return Page();
        }

        if (result.Outcome == AccountRegistrationOutcome.Invalid)
        {
            foreach (var violation in result.Violations)
            {
                ModelState.AddModelError(violation.Field, violation.Message);
            }

            return Page();
        }

        // Redirect after a successful POST so refreshing the page does not resubmit.
        TempData["SignupMessage"] =
            $"{UserDisplayIdentifier.Format(result.UserId!.Value)} 가입 완료";
        return RedirectToPage("/Index");
    }
}
