using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum AccountAuthenticationOutcome
{
    Authenticated,
    InvalidCredentials,
}

public sealed record AccountAuthenticationResult(
    AccountAuthenticationOutcome Outcome,
    long? UserId = null);

public sealed class AccountAuthenticationService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;

    public AccountAuthenticationService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<AccountAuthenticationResult> AuthenticateAsync(
        string email,
        string password)
    {
        if (!AccountEmailAddressPolicy.TryCreate(email, out var accountEmail, out _))
        {
            return new AccountAuthenticationResult(
                AccountAuthenticationOutcome.InvalidCredentials);
        }

        var user = await _db.Users.SingleOrDefaultAsync(
            item => item.NormalizedEmail == accountEmail!.NormalizedValue);

        var verified = user is not null
            && _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password)
                != PasswordVerificationResult.Failed;

        if (!verified)
        {
            return new AccountAuthenticationResult(
                AccountAuthenticationOutcome.InvalidCredentials);
        }

        return new AccountAuthenticationResult(
            AccountAuthenticationOutcome.Authenticated,
            user!.Id);
    }
}
