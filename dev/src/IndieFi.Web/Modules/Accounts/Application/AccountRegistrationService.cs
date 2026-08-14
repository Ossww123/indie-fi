using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
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
    long? UserId = null);

public sealed class AccountRegistrationService
{
    public const string EmailField = "Email";
    public const string PasswordField = "Password";

    /// <summary>PostgreSQL SQLSTATE for a unique constraint violation.</summary>
    private const string UniqueViolation = "23505";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly EmailProtectionService _emailProtection;
    private readonly EmailConfirmationTokenService _emailConfirmationTokens;
    private readonly AuthenticationEmailOutboxService _authenticationEmailOutbox;

    public AccountRegistrationService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        EmailProtectionService emailProtection,
        EmailConfirmationTokenService emailConfirmationTokens,
        AuthenticationEmailOutboxService authenticationEmailOutbox)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _emailProtection = emailProtection;
        _emailConfirmationTokens = emailConfirmationTokens;
        _authenticationEmailOutbox = authenticationEmailOutbox;
    }

    public async Task<AccountRegistrationResult> RegisterAsync(
        string? email,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var violations = Validate(email, password, out var accountEmail);
        if (violations.Count > 0)
        {
            return new AccountRegistrationResult(
                AccountRegistrationOutcome.Invalid,
                violations);
        }

        var validEmail = accountEmail!;
        var protectedEmail = _emailProtection.Protect(validEmail);
        var user = new User
        {
            EmailCiphertext = protectedEmail.Ciphertext,
            EmailNonce = protectedEmail.Nonce,
            EmailAuthenticationTag = protectedEmail.AuthenticationTag,
            EmailEncryptionKeyId = protectedEmail.KeyId,
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, password!);

        foreach (var lookup in _emailProtection.CreateWritableLookupValues(validEmail))
        {
            _db.UserEmailLookups.Add(new UserEmailLookup
            {
                User = user,
                KeyId = lookup.KeyId,
                Value = lookup.Value,
            });
        }

        // The database trigger derives the balance from the ledger. Because application
        // code writes only the ledger, there is no code path that can make them diverge.
        _db.Users.Add(user);
        _db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            User = user,
            Kind = CreditEntryKind.Grant,
            Delta = CreditPolicy.NewAccountGrant,
        });

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        try
        {
            // The first save obtains the database-generated user ID needed to bind the
            // encrypted Outbox payload to this account. The surrounding transaction keeps
            // this intermediate state invisible until the token and message also exist.
            await _db.SaveChangesAsync(cancellationToken);

            var issue = _emailConfirmationTokens.StageForNewAccount(user.Id);
            _authenticationEmailOutbox.StageEmailConfirmation(
                user.Id,
                issue.Token!,
                issue.ExpiresAt!.Value);

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            // Two requests can both pass an application-side availability check.
            // The unique index is the final authority for which registration wins.
            return new AccountRegistrationResult(
                AccountRegistrationOutcome.EmailAlreadyRegistered,
                NoViolations);
        }

        return new AccountRegistrationResult(
            AccountRegistrationOutcome.Registered,
            NoViolations,
            user.Id);
    }

    /// <summary>
    /// Enforces the account rules for every caller because nothing below this method
    /// checks the password length or the shape of the address.
    /// </summary>
    private static List<ValidationViolation> Validate(
        string? email,
        string? password,
        out AccountEmailAddress? accountEmail)
    {
        var violations = new List<ValidationViolation>();

        if (!AccountEmailAddressPolicy.TryCreate(email, out accountEmail, out var emailError))
        {
            violations.Add(new ValidationViolation(
                EmailField,
                emailError switch
                {
                    AccountEmailAddressError.Required => "이메일을 입력해 주세요.",
                    AccountEmailAddressError.TooLong => "이메일은 254자 이하여야 합니다.",
                    _ => "사용할 수 없는 이메일 형식입니다.",
                }));
        }

        if (!AccountPasswordPolicy.TryValidate(password, out var passwordError))
        {
            violations.Add(new ValidationViolation(
                PasswordField,
                passwordError switch
                {
                    AccountPasswordError.Required => "비밀번호를 입력해 주세요.",
                    AccountPasswordError.TooLong => "비밀번호는 128자 이하여야 합니다.",
                    _ => "비밀번호는 8자 이상이어야 합니다.",
                }));
        }

        return violations;
    }
}
