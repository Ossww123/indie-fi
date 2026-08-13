using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Services;

public enum AccountRegistrationOutcome
{
    Registered,
    EmailAlreadyRegistered,
}

public sealed record AccountRegistrationResult(
    AccountRegistrationOutcome Outcome,
    long? UserId = null,
    string? Email = null);

public sealed class AccountRegistrationService
{
    /// <summary>PostgreSQL SQLSTATE for a unique constraint violation.</summary>
    private const string UniqueViolation = "23505";

    // Users earn credits by giving feedback on other users' requests, but no one
    // can create a request while everyone has zero credits, so this grant starts the cycle.
    private const int SignupGrant = 3;

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
        string email,
        string password)
    {
        var user = new User { Email = email };
        user.PasswordHash = _passwordHasher.HashPassword(user, password);

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
                AccountRegistrationOutcome.EmailAlreadyRegistered);
        }

        return new AccountRegistrationResult(
            AccountRegistrationOutcome.Registered,
            user.Id,
            user.Email);
    }
}
