using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum PasswordResetCompletionOutcome
{
    Completed,
    InvalidToken,
    InvalidNewPassword,
}

public sealed record PasswordResetCompletionResult(
    PasswordResetCompletionOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations);

public sealed class PasswordResetCompletionService
{
    public const string NewPasswordField = "NewPassword";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly TimeProvider _timeProvider;

    public PasswordResetCompletionService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        TimeProvider timeProvider)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _timeProvider = timeProvider;
    }

    public async Task<PasswordResetCompletionResult> CompleteAsync(
        string? plaintextToken,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!AccountPasswordPolicy.TryValidate(newPassword, out var passwordError))
        {
            return new PasswordResetCompletionResult(
                PasswordResetCompletionOutcome.InvalidNewPassword,
                [new ValidationViolation(NewPasswordField, GetPasswordError(passwordError))]);
        }

        if (!TryHashToken(plaintextToken, out var tokenHash))
        {
            return InvalidToken();
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                cancellationToken);

            // Discover the stable account row first, then lock it before deciding whether
            // the token is still usable. Issuance follows the same account-first lock order.
            var candidate = await _db.PasswordResetTokens
                .Where(token => token.TokenHash.SequenceEqual(tokenHash))
                .Select(token => new { token.Id, token.UserId })
                .SingleOrDefaultAsync(cancellationToken);

            if (candidate is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return InvalidToken();
            }

            var users = await _db.Users
                .FromSqlInterpolated(
                    $"SELECT * FROM users WHERE id = {candidate.UserId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            var user = users.SingleOrDefault();

            if (user?.PasswordHash is null
                || !AccountStatusPolicy.AllowsMutation(
                    user.AccountStatus,
                    user.SuspendedUntil))
            {
                await transaction.CommitAsync(cancellationToken);
                return InvalidToken();
            }

            var now = _timeProvider.GetUtcNow();
            var consumedCount = await _db.PasswordResetTokens
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
                return InvalidToken();
            }

            await _db.PasswordResetTokens
                .Where(token =>
                    token.UserId == user.Id
                    && token.Id != candidate.Id
                    && token.UsedAt == null
                    && token.RevokedAt == null)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(token => token.RevokedAt, now),
                    cancellationToken);

            user.PasswordHash = _passwordHasher.HashPassword(user, newPassword!);
            user.SessionVersion = checked(user.SessionVersion + 1);

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordResetCompletionResult(
                PasswordResetCompletionOutcome.Completed,
                NoViolations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    private static PasswordResetCompletionResult InvalidToken() =>
        new(PasswordResetCompletionOutcome.InvalidToken, NoViolations);

    private static string GetPasswordError(AccountPasswordError error) => error switch
    {
        AccountPasswordError.Required => "새 비밀번호를 입력해 주세요.",
        AccountPasswordError.TooLong => "새 비밀번호는 128자 이하여야 합니다.",
        _ => "새 비밀번호는 8자 이상이어야 합니다.",
    };

    private static bool TryHashToken(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(token)
            || token.Length != PasswordResetTokenService.EncodedTokenLength)
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
            if (decoded.Length != PasswordResetTokenService.TokenEntropySize)
            {
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(token);
            try
            {
                hash = SHA256.HashData(bytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }
}
