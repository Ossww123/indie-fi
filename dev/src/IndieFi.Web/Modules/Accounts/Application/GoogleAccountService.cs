using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum GoogleAccountOutcome
{
    Authenticated,
    Created,
    RequiresPasswordLink,
    LinkUnavailable,
    AccountUnavailable,
}

public sealed record GoogleAccountResult(
    GoogleAccountOutcome Outcome,
    long? UserId = null,
    long? SessionVersion = null);

public enum GoogleAccountLinkOutcome
{
    Linked,
    InvalidPassword,
    Unavailable,
    AccountUnavailable,
}

public sealed record GoogleAccountLinkResult(
    GoogleAccountLinkOutcome Outcome,
    long? UserId = null,
    long? SessionVersion = null);

public sealed class GoogleAccountService
{
    private const string UniqueViolation = "23505";

    private readonly AppDbContext _db;
    private readonly EmailProtectionService _emailProtection;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly TimeProvider _timeProvider;

    public GoogleAccountService(
        AppDbContext db,
        EmailProtectionService emailProtection,
        IPasswordHasher<User> passwordHasher,
        TimeProvider timeProvider)
    {
        _db = db;
        _emailProtection = emailProtection;
        _passwordHasher = passwordHasher;
        _timeProvider = timeProvider;
    }

    public async Task<GoogleAccountResult> ResolveAsync(
        string providerUserId,
        AccountEmailAddress email,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUserId);
        ArgumentNullException.ThrowIfNull(email);

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        try
        {
            var linkedUserId = await FindLinkedUserIdAsync(
                providerUserId,
                cancellationToken);
            if (linkedUserId is not null)
            {
                var result = await PrepareLinkedAccountAsync(
                    linkedUserId.Value,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }

            var existingUserId = await FindUserIdByEmailAsync(
                email,
                cancellationToken);
            if (existingUserId is not null)
            {
                var collisionOutcome = await GetEmailCollisionOutcomeAsync(
                    existingUserId.Value,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new GoogleAccountResult(collisionOutcome);
            }

            var protectedEmail = _emailProtection.Protect(email);
            var user = new User
            {
                EmailCiphertext = protectedEmail.Ciphertext,
                EmailNonce = protectedEmail.Nonce,
                EmailAuthenticationTag = protectedEmail.AuthenticationTag,
                EmailEncryptionKeyId = protectedEmail.KeyId,
                PasswordHash = null,
                EmailConfirmedAt = _timeProvider.GetUtcNow(),
            };
            _db.Users.Add(user);

            foreach (var lookup in _emailProtection.CreateWritableLookupValues(email))
            {
                _db.UserEmailLookups.Add(new UserEmailLookup
                {
                    User = user,
                    KeyId = lookup.KeyId,
                    Value = lookup.Value,
                });
            }

            _db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                User = user,
                Kind = CreditEntryKind.Grant,
                Delta = CreditPolicy.NewAccountGrant,
            });

            // The first save obtains the user ID. The deferred authentication-method
            // trigger permits this intermediate passwordless row inside the transaction.
            await _db.SaveChangesAsync(cancellationToken);
            _db.ExternalLogins.Add(new ExternalLogin
            {
                UserId = user.Id,
                Provider = ExternalLoginPolicy.GoogleProvider,
                ProviderUserId = providerUserId,
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GoogleAccountResult(
                GoogleAccountOutcome.Created,
                user.Id,
                user.SessionVersion);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();

            // Concurrent callbacks may race after both observe no account. Resolve the
            // winning rows instead of creating a second reward or auto-linking by email.
            var winner = await ResolveConcurrentWinnerAsync(
                providerUserId,
                email,
                cancellationToken);
            if (winner is not null)
            {
                return winner;
            }

            throw;
        }
    }

    public async Task<GoogleAccountOutcome> GetLinkAvailabilityAsync(
        string providerUserId,
        AccountEmailAddress email,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUserId);
        ArgumentNullException.ThrowIfNull(email);

        if (await FindLinkedUserIdAsync(providerUserId, cancellationToken) is not null)
        {
            return GoogleAccountOutcome.LinkUnavailable;
        }

        var userId = await FindUserIdByEmailAsync(email, cancellationToken);
        return userId is null
            ? GoogleAccountOutcome.LinkUnavailable
            : await GetEmailCollisionOutcomeAsync(userId.Value, cancellationToken);
    }

    public async Task<GoogleAccountLinkResult> LinkAsync(
        string providerUserId,
        AccountEmailAddress email,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUserId);
        ArgumentNullException.ThrowIfNull(email);

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        try
        {
            var userId = await FindUserIdByEmailAsync(email, cancellationToken);
            if (userId is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return UnavailableLink();
            }

            var users = await _db.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE id = {userId.Value} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var user = users.SingleOrDefault();
            if (user?.PasswordHash is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return UnavailableLink();
            }

            if (string.IsNullOrEmpty(password)
                || _passwordHasher.VerifyHashedPassword(
                    user,
                    user.PasswordHash,
                    password) == PasswordVerificationResult.Failed)
            {
                await transaction.CommitAsync(cancellationToken);
                return new GoogleAccountLinkResult(
                    GoogleAccountLinkOutcome.InvalidPassword);
            }

            var accessState = AccountStatusPolicy.Evaluate(
                user.AccountStatus,
                user.SuspendedUntil,
                _timeProvider.GetUtcNow());
            if (accessState == AccountAccessState.Suspended)
            {
                await transaction.CommitAsync(cancellationToken);
                return UnavailableAccountLink();
            }

            var linkedUserId = await FindLinkedUserIdAsync(
                providerUserId,
                cancellationToken);
            if (linkedUserId is not null)
            {
                if (linkedUserId != user.Id)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return UnavailableLink();
                }

                ReactivateIfExpired(user, accessState);
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Linked(user);
            }

            if (await _db.ExternalLogins.AsNoTracking().AnyAsync(
                    login => login.UserId == user.Id
                        && login.Provider == ExternalLoginPolicy.GoogleProvider,
                    cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return UnavailableLink();
            }

            _db.ExternalLogins.Add(new ExternalLogin
            {
                UserId = user.Id,
                Provider = ExternalLoginPolicy.GoogleProvider,
                ProviderUserId = providerUserId,
            });
            ReactivateIfExpired(user, accessState);
            user.EmailConfirmedAt ??= _timeProvider.GetUtcNow();
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Linked(user);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();

            return await ResolveConcurrentLinkAsync(
                providerUserId,
                email,
                cancellationToken);
        }
    }

    private async Task<long?> FindLinkedUserIdAsync(
        string providerUserId,
        CancellationToken cancellationToken)
    {
        return await _db.ExternalLogins
            .AsNoTracking()
            .Where(login =>
                login.Provider == ExternalLoginPolicy.GoogleProvider
                && login.ProviderUserId == providerUserId)
            .Select(login => (long?)login.UserId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<User?> LockUserAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return users.SingleOrDefault();
    }

    private async Task<GoogleAccountResult> PrepareLinkedAccountAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var user = await LockUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return new GoogleAccountResult(GoogleAccountOutcome.LinkUnavailable);
        }

        var accessState = AccountStatusPolicy.Evaluate(
            user.AccountStatus,
            user.SuspendedUntil,
            _timeProvider.GetUtcNow());
        if (accessState == AccountAccessState.Suspended)
        {
            return new GoogleAccountResult(GoogleAccountOutcome.AccountUnavailable);
        }

        ReactivateIfExpired(user, accessState);
        await _db.SaveChangesAsync(cancellationToken);
        return Authenticated(user);
    }

    private async Task<GoogleAccountResult?> ResolveConcurrentWinnerAsync(
        string providerUserId,
        AccountEmailAddress email,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var linkedUserId = await FindLinkedUserIdAsync(
            providerUserId,
            cancellationToken);
        if (linkedUserId is not null)
        {
            var result = await PrepareLinkedAccountAsync(
                linkedUserId.Value,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        var existingUserId = await FindUserIdByEmailAsync(email, cancellationToken);
        if (existingUserId is not null)
        {
            var outcome = await GetEmailCollisionOutcomeAsync(
                existingUserId.Value,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GoogleAccountResult(outcome);
        }

        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    private async Task<GoogleAccountLinkResult> ResolveConcurrentLinkAsync(
        string providerUserId,
        AccountEmailAddress email,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var linkedUserId = await FindLinkedUserIdAsync(
            providerUserId,
            cancellationToken);
        var userId = await FindUserIdByEmailAsync(email, cancellationToken);
        if (linkedUserId is null || linkedUserId != userId)
        {
            await transaction.CommitAsync(cancellationToken);
            return UnavailableLink();
        }

        var user = await LockUserAsync(linkedUserId.Value, cancellationToken);
        if (user is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return UnavailableLink();
        }

        var accessState = AccountStatusPolicy.Evaluate(
            user.AccountStatus,
            user.SuspendedUntil,
            _timeProvider.GetUtcNow());
        if (accessState == AccountAccessState.Suspended)
        {
            await transaction.CommitAsync(cancellationToken);
            return UnavailableAccountLink();
        }

        ReactivateIfExpired(user, accessState);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Linked(user);
    }

    private async Task<long?> FindUserIdByEmailAsync(
        AccountEmailAddress email,
        CancellationToken cancellationToken)
    {
        long? userId = null;
        foreach (var lookup in _emailProtection.CreateReadableLookupValues(email))
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

        return userId;
    }

    private async Task<GoogleAccountOutcome> GetEmailCollisionOutcomeAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var account = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                HasPassword = user.PasswordHash != null,
                user.AccountStatus,
                user.SuspendedUntil,
                HasGoogleLogin = _db.ExternalLogins.Any(login =>
                    login.UserId == user.Id
                    && login.Provider == ExternalLoginPolicy.GoogleProvider),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (account is null)
        {
            return GoogleAccountOutcome.LinkUnavailable;
        }

        if (AccountStatusPolicy.Evaluate(
                account.AccountStatus,
                account.SuspendedUntil,
                _timeProvider.GetUtcNow()) == AccountAccessState.Suspended)
        {
            return GoogleAccountOutcome.AccountUnavailable;
        }

        return account is { HasPassword: true, HasGoogleLogin: false }
            ? GoogleAccountOutcome.RequiresPasswordLink
            : GoogleAccountOutcome.LinkUnavailable;
    }

    private static GoogleAccountResult Authenticated(User user) =>
        new(
            GoogleAccountOutcome.Authenticated,
            user.Id,
            user.SessionVersion);

    private static void ReactivateIfExpired(
        User user,
        AccountAccessState accessState)
    {
        if (accessState == AccountAccessState.TemporarySuspensionExpired)
        {
            AccountStatusTransitionService.ApplyTransition(
                user,
                AccountStatus.Active,
                suspendedUntil: null);
        }
    }

    private static GoogleAccountLinkResult Linked(User user) =>
        new(
            GoogleAccountLinkOutcome.Linked,
            user.Id,
            user.SessionVersion);

    private static GoogleAccountLinkResult UnavailableLink() =>
        new(GoogleAccountLinkOutcome.Unavailable);

    private static GoogleAccountLinkResult UnavailableAccountLink() =>
        new(GoogleAccountLinkOutcome.AccountUnavailable);
}
