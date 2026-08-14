using System.Buffers.Binary;
using System.Net;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum PasswordResetRequestOutcome
{
    RequestAccepted,
    InvalidEmail,
}

public sealed record PasswordResetRequestResult(
    PasswordResetRequestOutcome Outcome,
    string? EmailError = null);

public sealed class PasswordResetRequestService
{
    public const string EmailField = "Email";
    public const int AccountHourlyLimit = 5;
    public const int IpHourlyLimit = 20;

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;
    private readonly EmailProtectionService _emailProtection;
    private readonly RequestIpProtectionService _requestIpProtection;
    private readonly PasswordResetTokenService _passwordResetTokens;
    private readonly AuthenticationEmailOutboxService _authenticationEmailOutbox;
    private readonly TimeProvider _timeProvider;

    public PasswordResetRequestService(
        AppDbContext db,
        EmailProtectionService emailProtection,
        RequestIpProtectionService requestIpProtection,
        PasswordResetTokenService passwordResetTokens,
        AuthenticationEmailOutboxService authenticationEmailOutbox,
        TimeProvider timeProvider)
    {
        _db = db;
        _emailProtection = emailProtection;
        _requestIpProtection = requestIpProtection;
        _passwordResetTokens = passwordResetTokens;
        _authenticationEmailOutbox = authenticationEmailOutbox;
        _timeProvider = timeProvider;
    }

    public async Task<PasswordResetRequestResult> RequestAsync(
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
            return new PasswordResetRequestResult(
                PasswordResetRequestOutcome.InvalidEmail,
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

        // Attempts outside the rolling decision window no longer carry security value.
        // Delete them before taking any advisory lock to keep the lock order stable.
        await _db.PasswordResetRequestAttempts
            .Where(attempt => attempt.CreatedAt <= cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        // Lock every readable key version so deployments that overlap a key rotation
        // still serialize decisions for the same normalized IP address.
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
            ipRequestCount += await _db.PasswordResetRequestAttempts.CountAsync(
                attempt =>
                    attempt.RequestIpHashKeyId == hash.KeyId
                    && attempt.RequestIpHash.SequenceEqual(hash.Value)
                    && attempt.CreatedAt > cutoff,
                cancellationToken);
        }

        // A caller must not learn whether the request was limited, so a valid email
        // shape always receives the same application result.
        if (ipRequestCount >= IpHourlyLimit)
        {
            await transaction.CommitAsync(cancellationToken);
            return Accepted();
        }

        var userId = await FindUserIdAsync(accountEmail!, cancellationToken);
        User? user = null;
        if (userId is long existingUserId)
        {
            user = await LockUserAsync(existingUserId, cancellationToken);
        }

        var queueEmail = false;
        if (user is { EmailConfirmedAt: not null }
            && !string.IsNullOrEmpty(user.PasswordHash))
        {
            var accountRequestCount = await _db.PasswordResetRequestAttempts.CountAsync(
                attempt =>
                    attempt.UserId == user.Id
                    && attempt.CreatedAt > cutoff,
                cancellationToken);
            var lastIssuedAt = await _db.PasswordResetTokens
                .Where(token => token.UserId == user.Id)
                .MaxAsync(token => (DateTimeOffset?)token.CreatedAt, cancellationToken);

            queueEmail = accountRequestCount < AccountHourlyLimit
                && (lastIssuedAt is null
                    || lastIssuedAt <= now.Subtract(MinimumInterval));
        }

        _db.PasswordResetRequestAttempts.Add(new PasswordResetRequestAttempt
        {
            UserId = queueEmail ? user!.Id : null,
            RequestIpHash = currentIpHash.Value,
            RequestIpHashKeyId = currentIpHash.KeyId,
            CreatedAt = now,
        });

        if (queueEmail)
        {
            var issue = await _passwordResetTokens.StageReplacementForLockedAccountAsync(
                user!.Id,
                now,
                cancellationToken);
            await _authenticationEmailOutbox.ReplacePasswordResetAsync(
                user.Id,
                issue.Token!,
                issue.ExpiresAt!.Value,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Accepted();
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

    private static PasswordResetRequestResult Accepted() =>
        new(PasswordResetRequestOutcome.RequestAccepted);
}
