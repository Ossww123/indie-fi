using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum EmailConfirmationTokenIssueOutcome
{
    Issued,
    UserNotFound,
    EmailAlreadyConfirmed,
    AccountUnavailable,
}

public sealed record EmailConfirmationTokenIssueResult(
    EmailConfirmationTokenIssueOutcome Outcome,
    string? Token = null,
    DateTimeOffset? ExpiresAt = null);

public enum EmailConfirmationTokenConsumeOutcome
{
    Confirmed,
    Invalid,
}

public sealed record EmailConfirmationTokenConsumeResult(
    EmailConfirmationTokenConsumeOutcome Outcome);

public sealed class EmailConfirmationTokenService
{
    public const int TokenEntropySize = 32;
    public const int EncodedTokenLength = 43;

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public EmailConfirmationTokenService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<EmailConfirmationTokenIssueResult> IssueAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        var user = await LockUserAsync(userId, cancellationToken);

        if (user is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new EmailConfirmationTokenIssueResult(
                EmailConfirmationTokenIssueOutcome.UserNotFound);
        }

        if (user.EmailConfirmedAt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new EmailConfirmationTokenIssueResult(
                EmailConfirmationTokenIssueOutcome.EmailAlreadyConfirmed);
        }

        if (!AccountStatusPolicy.AllowsMutation(
                user.AccountStatus,
                user.SuspendedUntil))
        {
            await transaction.CommitAsync(cancellationToken);
            return new EmailConfirmationTokenIssueResult(
                EmailConfirmationTokenIssueOutcome.AccountUnavailable);
        }

        var issue = await StageReplacementForLockedAccountAsync(
            userId,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return issue;
    }

    internal async Task<EmailConfirmationTokenIssueResult>
        StageReplacementForLockedAccountAsync(
            long userId,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken)
    {
        await _db.EmailConfirmationTokens
            .Where(token =>
                token.UserId == userId
                && token.UsedAt == null
                && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(token => token.RevokedAt, createdAt),
                cancellationToken);

        return StageToken(userId, createdAt);
    }

    internal EmailConfirmationTokenIssueResult StageForNewAccount(long userId)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        return StageToken(userId, _timeProvider.GetUtcNow());
    }

    public async Task<EmailConfirmationTokenConsumeResult> ConsumeAsync(
        string? plaintextToken,
        CancellationToken cancellationToken = default)
    {
        if (!TryHashToken(plaintextToken, out var tokenHash))
        {
            return new EmailConfirmationTokenConsumeResult(
                EmailConfirmationTokenConsumeOutcome.Invalid);
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                cancellationToken);

            // The first lookup discovers which stable user row coordinates this token.
            // Validity is checked again only after that row is locked, so a concurrent
            // reissue cannot revoke the token between validation and consumption.
            var candidate = await _db.EmailConfirmationTokens
                .Where(token => token.TokenHash.SequenceEqual(tokenHash))
                .Select(token => new { token.Id, token.UserId })
                .SingleOrDefaultAsync(cancellationToken);

            if (candidate is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new EmailConfirmationTokenConsumeResult(
                    EmailConfirmationTokenConsumeOutcome.Invalid);
            }

            var user = await LockUserAsync(candidate.UserId, cancellationToken);
            if (user is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new EmailConfirmationTokenConsumeResult(
                    EmailConfirmationTokenConsumeOutcome.Invalid);
            }

            if (!AccountStatusPolicy.AllowsMutation(
                    user.AccountStatus,
                    user.SuspendedUntil))
            {
                await transaction.CommitAsync(cancellationToken);
                return new EmailConfirmationTokenConsumeResult(
                    EmailConfirmationTokenConsumeOutcome.Invalid);
            }

            var now = _timeProvider.GetUtcNow();
            var consumedCount = await _db.EmailConfirmationTokens
                .Where(token =>
                    token.Id == candidate.Id
                    && token.TokenHash.SequenceEqual(tokenHash)
                    && token.ExpiresAt > now
                    && token.UsedAt == null
                    && token.RevokedAt == null)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(token => token.UsedAt, now),
                    cancellationToken);

            if (consumedCount == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new EmailConfirmationTokenConsumeResult(
                    EmailConfirmationTokenConsumeOutcome.Invalid);
            }

            // Preserve an earlier trusted confirmation time, for example one supplied by
            // an external identity provider, while still consuming this valid token once.
            await _db.Users
                .Where(account =>
                    account.Id == candidate.UserId
                    && account.EmailConfirmedAt == null)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(account => account.EmailConfirmedAt, now),
                    cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new EmailConfirmationTokenConsumeResult(
                EmailConfirmationTokenConsumeOutcome.Confirmed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    private async Task<User?> LockUserAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        // Issuance and consumption both lock users before changing tokens. The common
        // order serializes operations for one account without creating an opposite-order
        // deadlock between the users and email_confirmation_tokens tables.
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return users.SingleOrDefault();
    }

    private static string CreatePlaintextToken()
    {
        var entropy = RandomNumberGenerator.GetBytes(TokenEntropySize);
        try
        {
            return WebEncoders.Base64UrlEncode(entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private EmailConfirmationTokenIssueResult StageToken(
        long userId,
        DateTimeOffset createdAt)
    {
        var plaintextToken = CreatePlaintextToken();
        var expiresAt = createdAt.Add(TokenLifetime);
        _db.EmailConfirmationTokens.Add(new EmailConfirmationToken
        {
            UserId = userId,
            TokenHash = HashToken(plaintextToken),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        });

        return new EmailConfirmationTokenIssueResult(
            EmailConfirmationTokenIssueOutcome.Issued,
            plaintextToken,
            expiresAt);
    }

    private static byte[] HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryHashToken(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(token) || token.Length != EncodedTokenLength)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (decoded.Length != TokenEntropySize)
            {
                return false;
            }

            hash = HashToken(token);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }
}
