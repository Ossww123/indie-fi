using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum AccountRegistrationOutcome
{
    Registered,
    EmailAlreadyRegistered,
    Invalid,
}

public sealed record AccountRegistrationResult(
    AccountRegistrationOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations,
    long? UserId = null,
    string? Email = null);

public sealed class AccountRegistrationService
{
    public const string EmailField = "Email";
    public const string PasswordField = "Password";

    /// <summary>PostgreSQL SQLSTATE for a unique constraint violation.</summary>
    private const string UniqueViolation = "23505";

    // Users earn credits by giving feedback on other users' requests, but no one
    // can create a request while everyone has zero credits, so this grant starts the cycle.
    private const int SignupGrant = 3;

    private const int EmailMaxLength = 320;
    private const int PasswordMinLength = 8;
    private const int PasswordMaxLength = 128;

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;

    public AccountRegistrationService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<AccountRegistrationResult> RegisterAsync(
        string? email,
        string? password)
    {
        var violations = Validate(email, password);
        if (violations.Count > 0)
        {
            return new AccountRegistrationResult(
                AccountRegistrationOutcome.Invalid,
                violations);
        }

        var user = new User { Email = email! };
        user.PasswordHash = _passwordHasher.HashPassword(user, password!);

        // The database trigger derives the balance from the ledger. Because application
        // code writes only the ledger, there is no code path that can make them diverge.
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
            // Two requests can both pass an application-side availability check.
            // The unique index is the final authority for which registration wins.
            return new AccountRegistrationResult(
                AccountRegistrationOutcome.EmailAlreadyRegistered,
                NoViolations);
        }

        return new AccountRegistrationResult(
            AccountRegistrationOutcome.Registered,
            NoViolations,
            user.Id,
            user.Email);
    }

    /// <summary>
    /// Enforces the account rules for every caller because nothing below this method
    /// checks the password length or the shape of the address.
    /// </summary>
    private static List<ValidationViolation> Validate(string? email, string? password)
    {
        var violations = new List<ValidationViolation>();

        if (string.IsNullOrWhiteSpace(email))
        {
            violations.Add(new ValidationViolation(
                EmailField,
                "이메일을 입력해 주세요."));
        }
        else if (email.Length > EmailMaxLength)
        {
            violations.Add(new ValidationViolation(
                EmailField,
                "이메일이 너무 깁니다."));
        }
        else if (!new EmailAddressAttribute().IsValid(email))
        {
            violations.Add(new ValidationViolation(
                EmailField,
                "이메일 형식이 아닙니다."));
        }

        if (string.IsNullOrEmpty(password))
        {
            violations.Add(new ValidationViolation(
                PasswordField,
                "비밀번호를 입력해 주세요."));
        }
        else if (password.Length is < PasswordMinLength or > PasswordMaxLength)
        {
            violations.Add(new ValidationViolation(
                PasswordField,
                "비밀번호는 8자 이상이어야 합니다."));
        }

        return violations;
    }
}
