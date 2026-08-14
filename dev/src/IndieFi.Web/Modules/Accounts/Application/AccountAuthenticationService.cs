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
    private readonly EmailProtectionService _emailProtection;

    public AccountAuthenticationService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        EmailProtectionService emailProtection)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _emailProtection = emailProtection;
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

        User? user = null;
        foreach (var lookup in
                 _emailProtection.CreateReadableLookupValues(accountEmail!))
        {
            var candidate = await _db.UserEmailLookups
                .Where(item =>
                    item.KeyId == lookup.KeyId
                    && item.Value.SequenceEqual(lookup.Value))
                .Select(item => item.User)
                .SingleOrDefaultAsync();

            if (candidate is null)
            {
                continue;
            }

            if (user is not null && user.Id != candidate.Id)
            {
                throw new InvalidOperationException(
                    "Email lookup key versions resolved to different users.");
            }

            user = candidate;
        }

        var verified = user?.PasswordHash is not null
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
