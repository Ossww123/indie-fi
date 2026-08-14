using System.Buffers.Binary;
using System.Net;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum EmailConfirmationResendOutcome
{
    EmailQueued,
    RequestAccepted,
    AccountRateLimited,
    IpRateLimited,
    InvalidEmail,
}

public sealed record EmailConfirmationResendResult(
    EmailConfirmationResendOutcome Outcome,
    string? EmailError = null);

public sealed class EmailConfirmationResendService
{
    public const string EmailField = "Email";
    public const int AccountHourlyLimit = 5;
    public const int IpHourlyLimit = 20;

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;
    private readonly EmailProtectionService _emailProtection;
    private readonly RequestIpProtectionService _requestIpProtection;
    private readonly EmailConfirmationTokenService _emailConfirmationTokens;
    private readonly AuthenticationEmailOutboxService _authenticationEmailOutbox;
    private readonly TimeProvider _timeProvider;

    public EmailConfirmationResendService(
        AppDbContext db,
        EmailProtectionService emailProtection,
        RequestIpProtectionService requestIpProtection,
        EmailConfirmationTokenService emailConfirmationTokens,
        AuthenticationEmailOutboxService authenticationEmailOutbox,
        TimeProvider timeProvider)
    {
        _db = db;
        _emailProtection = emailProtection;
        _requestIpProtection = requestIpProtection;
        _emailConfirmationTokens = emailConfirmationTokens;
        _authenticationEmailOutbox = authenticationEmailOutbox;
        _timeProvider = timeProvider;
    }

    public async Task<EmailConfirmationResendResult> RequestAsync(
        string? email,
        IPAddress requestIpAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestIpAddress);

        if (!AccountEmailAddressPolicy.TryCreate(
                email,
                out var accountEmail,
                out var emailError))
        {
            return new EmailConfirmationResendResult(
                EmailConfirmationResendOutcome.InvalidEmail,
                emailError switch
                {
                    AccountEmailAddressError.Required => "이메일을 입력해 주세요.",
                    AccountEmailAddressError.TooLong => "이메일은 254자 이하여야 합니다.",
                    _ => "사용할 수 없는 이메일 형식입니다.",
                });
        }

        var now = _timeProvider.GetUtcNow();
        var cutoff = now.Subtract(RateLimitWindow);
        var currentIpHash = _requestIpProtection.CreateCurrentHash(requestIpAddress);
        var readableIpHashes = _requestIpProtection.CreateReadableHashes(requestIpAddress);

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        // These rows exist only for the rolling one-hour decision window. Cleanup runs
        // before any per-IP lock so different IPs cannot hold advisory locks while they
        // wait on the same expired database rows.
        await _db.EmailConfirmationResendAttempts
            .Where(attempt => attempt.CreatedAt <= cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        // Every app version that can read an IP key takes that key's lock. During key
        // rotation, old and new versions therefore still share at least one lock.
        foreach (var lockKey in readableIpHashes
                     .Select(hash => BinaryPrimitives.ReadInt64BigEndian(hash.Value))
                     .Distinct()
                     .Order())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})",
                cancellationToken);
        }

        var ipRequestCount = 0;
        foreach (var hash in readableIpHashes)
        {
            ipRequestCount += await _db.EmailConfirmationResendAttempts.CountAsync(
                attempt =>
                    attempt.RequestIpHashKeyId == hash.KeyId
                    && attempt.RequestIpHash.SequenceEqual(hash.Value)
                    && attempt.CreatedAt > cutoff,
                cancellationToken);
        }

        if (ipRequestCount >= IpHourlyLimit)
        {
            await transaction.CommitAsync(cancellationToken);
            return new EmailConfirmationResendResult(
                EmailConfirmationResendOutcome.IpRateLimited);
        }

        var userId = await FindUserIdAsync(accountEmail!, cancellationToken);
        User? user = null;
        if (userId is long existingUserId)
        {
            user = await LockUserAsync(existingUserId, cancellationToken);
        }

        var outcome = EmailConfirmationResendOutcome.RequestAccepted;
        var queueEmail = false;
        if (user is { EmailConfirmedAt: null })
        {
            var accountRequestCount = await _db.EmailConfirmationResendAttempts.CountAsync(
                attempt =>
                    attempt.UserId == user.Id
                    && attempt.CreatedAt > cutoff,
                cancellationToken);
            var lastIssuedAt = await _db.EmailConfirmationTokens
                .Where(token => token.UserId == user.Id)
                .MaxAsync(token => (DateTimeOffset?)token.CreatedAt, cancellationToken);

            if (accountRequestCount >= AccountHourlyLimit
                || lastIssuedAt > now.Subtract(MinimumInterval))
            {
                outcome = EmailConfirmationResendOutcome.AccountRateLimited;
            }
            else
            {
                queueEmail = true;
                outcome = EmailConfirmationResendOutcome.EmailQueued;
            }
        }

        _db.EmailConfirmationResendAttempts.Add(new EmailConfirmationResendAttempt
        {
            UserId = queueEmail ? user!.Id : null,
            RequestIpHash = currentIpHash.Value,
            RequestIpHashKeyId = currentIpHash.KeyId,
            CreatedAt = now,
        });

        if (queueEmail)
        {
            var issue = await _emailConfirmationTokens.StageReplacementForLockedAccountAsync(
                user!.Id,
                now,
                cancellationToken);
            await _authenticationEmailOutbox.ReplaceEmailConfirmationAsync(
                user.Id,
                issue.Token!,
                issue.ExpiresAt!.Value,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EmailConfirmationResendResult(outcome);
    }

    private async Task<long?> FindUserIdAsync(
        AccountEmailAddress email,
        CancellationToken cancellationToken)
    {
        long? userId = null;
        foreach (var lookup in _emailProtection.CreateReadableLookupValues(email))
        {
            var candidate = await _db.UserEmailLookups
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

    private async Task<User?> LockUserAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return users.SingleOrDefault();
    }
}
