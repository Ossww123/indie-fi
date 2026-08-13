using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Services;
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

    [BindProperty]
    [Required(ErrorMessage = "이메일을 입력해 주세요.")]
    [EmailAddress(ErrorMessage = "이메일 형식이 아닙니다.")]
    [StringLength(320, ErrorMessage = "이메일이 너무 깁니다.")]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "비밀번호를 입력해 주세요.")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "비밀번호는 8자 이상이어야 합니다.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _accountRegistrationService.RegisterAsync(Email, Password);

        if (result.Outcome == AccountRegistrationOutcome.EmailAlreadyRegistered)
        {
            ModelState.AddModelError(string.Empty, "이미 가입된 이메일입니다.");
            return Page();
        }

        // Redirect after a successful POST so refreshing the page does not resubmit.
        TempData["SignupMessage"] = $"{result.Email} 가입 완료";
        return RedirectToPage("/Index");
    }
}
