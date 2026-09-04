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
    EmailNotConfirmed,
    AccountUnavailable,
}

public sealed record AccountAuthenticationResult(
    AccountAuthenticationOutcome Outcome,
    long? UserId = null,
    long? SessionVersion = null);

public sealed class AccountAuthenticationService
{
    private static readonly User DummyPasswordUser = new();
    private static readonly string DummyPasswordHash =
        new PasswordHasher<User>().HashPassword(
            DummyPasswordUser,
            "IndieFi dummy password verification value");

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly EmailProtectionService _emailProtection;
    private readonly TimeProvider _timeProvider;

    public AccountAuthenticationService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        EmailProtectionService emailProtection,
        TimeProvider timeProvider)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _emailProtection = emailProtection;
        _timeProvider = timeProvider;
    }

    public async Task<AccountAuthenticationResult> AuthenticateAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!AccountEmailAddressPolicy.TryCreate(email, out var accountEmail, out _))
        {
            return new AccountAuthenticationResult(
                AccountAuthenticationOutcome.InvalidCredentials);
        }

        long? userId = null;
        foreach (var lookup in
                 _emailProtection.CreateReadableLookupValues(accountEmail!))
        {
            var candidate = await _db.UserEmailLookups
                .AsNoTracking()
                .Where(item =>
                    item.KeyId == lookup.KeyId
                    && item.Value.SequenceEqual(lookup.Value))
                .Select(item => (long?)item.UserId)
                .SingleOrDefaultAsync(cancellationToken);

            if (candidate is null)
            {
                continue;
            }

            if (userId is not null && userId != candidate)
            {
                throw new InvalidOperationException(
                    "Email lookup key versions resolved to different users.");
            }

            userId = candidate;
        }

        if (userId is null)
        {
            _ = _passwordHasher.VerifyHashedPassword(
                DummyPasswordUser,
                DummyPasswordHash,
                password);
            return new AccountAuthenticationResult(
                AccountAuthenticationOutcome.InvalidCredentials);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId.Value} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var user = users.SingleOrDefault();
        var verified = user?.PasswordHash is not null
            && _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password)
                != PasswordVerificationResult.Failed;

        if (!verified)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AccountAuthenticationResult(
                AccountAuthenticationOutcome.InvalidCredentials);
        }

        if (user!.EmailConfirmedAt is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AccountAuthenticationResult(
                AccountAuthenticationOutcome.EmailNotConfirmed);
        }

        var accessState = AccountStatusPolicy.Evaluate(
            user.AccountStatus,
            user.SuspendedUntil,
            _timeProvider.GetUtcNow());
        if (accessState == AccountAccessState.Suspended)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AccountAuthenticationResult(
                AccountAuthenticationOutcome.AccountUnavailable);
        }

        if (accessState == AccountAccessState.TemporarySuspensionExpired)
        {
            AccountStatusTransitionService.ApplyTransition(
                user,
                AccountStatus.Active,
                suspendedUntil: null);
            await _db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new AccountAuthenticationResult(
            AccountAuthenticationOutcome.Authenticated,
            user.Id,
            user.SessionVersion);
    }
}
