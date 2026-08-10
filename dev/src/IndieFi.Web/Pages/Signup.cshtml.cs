using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Pages;

public class SignupModel : PageModel
{
    /// <summary>PostgreSQL SQLSTATE for a unique constraint violation.</summary>
    private const string UniqueViolation = "23505";

    // Users earn credits by giving feedback on other users' requests, but no one
    // can create a request while everyone has zero credits, so this grant starts the cycle.
    private const int SignupGrant = 3;

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;

    public SignupModel(AppDbContext db, IPasswordHasher<User> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
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

        var user = new User { Email = Email };
        user.PasswordHash = _passwordHasher.HashPassword(user, Password);

        _db.Users.Add(user);
        _db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            User = user,
            Kind = CreditEntryKind.Grant,
            Delta = SignupGrant,
        });

        try
        {
            // Saving both rows together prevents an intermediate state where the
            // account exists but its signup grant does not.
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // The database is the only place that can settle this: two requests can
            // both pass an application-side "is this email taken?" check and still
            // race. The unique index on users.email is what actually decides.
            ModelState.AddModelError(string.Empty, "이미 가입된 이메일입니다.");
            return Page();
        }

        // Redirect after a successful POST so refreshing the page does not resubmit.
        TempData["SignupMessage"] = $"{user.Email} 가입 완료";
        return RedirectToPage("/Index");
    }
}
