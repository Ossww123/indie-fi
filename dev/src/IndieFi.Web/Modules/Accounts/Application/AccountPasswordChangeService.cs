using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum AccountPasswordChangeOutcome
{
    Changed,
    InvalidCurrentPassword,
    InvalidNewPassword,
    AccountUnavailable,
}

public sealed record AccountPasswordChangeResult(
    AccountPasswordChangeOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations);

public sealed class AccountPasswordChangeService
{
    public const string CurrentPasswordField = "CurrentPassword";
    public const string NewPasswordField = "NewPassword";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;

    public AccountPasswordChangeService(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<AccountPasswordChangeResult> ChangeAsync(
        long userId,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        if (!AccountPasswordPolicy.TryValidate(newPassword, out var passwordError))
        {
            return new AccountPasswordChangeResult(
                AccountPasswordChangeOutcome.InvalidNewPassword,
                [new ValidationViolation(NewPasswordField, GetPasswordError(passwordError))]);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        // Serializing changes on the stable account row prevents two requests that both
        // know the old password from replacing each other and incrementing the session
        // version only once.
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var user = users.SingleOrDefault();

        if (user?.PasswordHash is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AccountPasswordChangeResult(
                AccountPasswordChangeOutcome.AccountUnavailable,
                NoViolations);
        }

        if (string.IsNullOrEmpty(currentPassword)
            || _passwordHasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                currentPassword) == PasswordVerificationResult.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AccountPasswordChangeResult(
                AccountPasswordChangeOutcome.InvalidCurrentPassword,
                [new ValidationViolation(
                    CurrentPasswordField,
                    "현재 비밀번호가 올바르지 않습니다.")]);
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword!);
        user.SessionVersion = checked(user.SessionVersion + 1);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AccountPasswordChangeResult(
            AccountPasswordChangeOutcome.Changed,
            NoViolations);
    }

    private static string GetPasswordError(AccountPasswordError error) => error switch
    {
        AccountPasswordError.Required => "새 비밀번호를 입력해 주세요.",
        AccountPasswordError.TooLong => "새 비밀번호는 128자 이하여야 합니다.",
        _ => "새 비밀번호는 8자 이상이어야 합니다.",
    };
}
