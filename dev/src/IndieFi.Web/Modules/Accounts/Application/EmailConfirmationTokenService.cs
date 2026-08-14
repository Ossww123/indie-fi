using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum EmailConfirmationTokenIssueOutcome
{
    Issued,
    UserNotFound,
    EmailAlreadyConfirmed,
}

public sealed record EmailConfirmationTokenIssueResult(
    EmailConfirmationTokenIssueOutcome Outcome,
    string? Token = null,
    DateTimeOffset? ExpiresAt = null);

public sealed class EmailConfirmationTokenService
{
    public const int TokenEntropySize = 32;

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

        // Every issuer locks the same stable row before revoking and inserting. Two
        // concurrent resends therefore cannot each leave their own token unresolved.
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var user = users.SingleOrDefault();

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

        var now = _timeProvider.GetUtcNow();
        await _db.EmailConfirmationTokens
            .Where(token =>
                token.UserId == userId
                && token.UsedAt == null
                && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(token => token.RevokedAt, now),
                cancellationToken);

        var plaintextToken = CreatePlaintextToken();
        _db.EmailConfirmationTokens.Add(new EmailConfirmationToken
        {
            UserId = userId,
            TokenHash = HashToken(plaintextToken),
            CreatedAt = now,
            ExpiresAt = now.Add(TokenLifetime),
        });

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new EmailConfirmationTokenIssueResult(
            EmailConfirmationTokenIssueOutcome.Issued,
            plaintextToken,
            now.Add(TokenLifetime));
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
