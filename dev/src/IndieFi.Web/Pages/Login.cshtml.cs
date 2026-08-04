using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Pages;

public class LoginModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;

    public LoginModel(AppDbContext db, IPasswordHasher<User> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    [BindProperty]
    [Required(ErrorMessage = "이메일을 입력해 주세요.")]
    [EmailAddress(ErrorMessage = "이메일 형식이 아닙니다.")]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "비밀번호를 입력해 주세요.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var user = await _db.Users.SingleOrDefaultAsync(u => u.Email == Email);

        var verified = user is not null
            && _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, Password)
                != PasswordVerificationResult.Failed;

        if (!verified)
        {
            // Deliberately the same message either way: saying "no such account"
            // would let anyone probe which email addresses are registered.
            ModelState.AddModelError(string.Empty, "이메일 또는 비밀번호가 올바르지 않습니다.");
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user!.Id.ToString()),
            new(ClaimTypes.Name, user.Email),
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
