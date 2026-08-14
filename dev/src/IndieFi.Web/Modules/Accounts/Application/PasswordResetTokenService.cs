using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum PasswordResetTokenIssueOutcome
{
    Issued,
    UserNotFound,
    AccountUnavailable,
}

public sealed record PasswordResetTokenIssueResult(
    PasswordResetTokenIssueOutcome Outcome,
    string? Token = null,
    DateTimeOffset? ExpiresAt = null);

public sealed class PasswordResetTokenService
{
    public const int TokenEntropySize = 32;
    public const int EncodedTokenLength = 43;

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public PasswordResetTokenService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<PasswordResetTokenIssueResult> IssueAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        var user = await LockUserAsync(userId, cancellationToken);
        if (user is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PasswordResetTokenIssueResult(
                PasswordResetTokenIssueOutcome.UserNotFound);
        }

        if (!AccountStatusPolicy.AllowsMutation(
                user.AccountStatus,
                user.SuspendedUntil))
        {
            await transaction.CommitAsync(cancellationToken);
            return new PasswordResetTokenIssueResult(
                PasswordResetTokenIssueOutcome.AccountUnavailable);
        }

        var issue = await StageReplacementForLockedAccountAsync(
            userId,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return issue;
    }

    internal async Task<PasswordResetTokenIssueResult>
        StageReplacementForLockedAccountAsync(
            long userId,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken)
    {
        await _db.PasswordResetTokens
            .Where(token =>
                token.UserId == userId
                && token.UsedAt == null
                && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(token => token.RevokedAt, createdAt),
                cancellationToken);

        return StageToken(userId, createdAt);
    }

    private async Task<User?> LockUserAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        // Every replacement locks the stable user row before touching reset tokens.
        // Concurrent requests for one account therefore use the same lock order.
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return users.SingleOrDefault();
    }

    private PasswordResetTokenIssueResult StageToken(
        long userId,
        DateTimeOffset createdAt)
    {
        var plaintextToken = CreatePlaintextToken();
        var expiresAt = createdAt.Add(TokenLifetime);
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = HashToken(plaintextToken),
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
        });

        return new PasswordResetTokenIssueResult(
            PasswordResetTokenIssueOutcome.Issued,
            plaintextToken,
            expiresAt);
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
}
