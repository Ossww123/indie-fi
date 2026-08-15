using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Profiles.Application;

public enum UserProfileUpdateOutcome
{
    Updated,
    Invalid,
    NicknameUnavailable,
    NicknameChangeTooSoon,
    AccountUnavailable,
}

public sealed record UserProfileEditState(
    string? Nickname,
    string Biography,
    bool CanChangeNickname,
    DateTimeOffset? NicknameChangeAvailableAt);

public sealed record UserProfileUpdateResult(
    UserProfileUpdateOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations,
    DateTimeOffset? NicknameChangeAvailableAt = null);

public sealed class UserProfileManagementService
{
    public const string NicknameField = "Nickname";
    public const string BiographyField = "Biography";

    private const string UniqueViolation = "23505";
    private const string NormalizedNicknameIndex =
        "IX_user_profiles_normalized_nickname";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public UserProfileManagementService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<UserProfileEditState?> GetForEditAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        if (!await _db.Users
                .AsNoTracking()
                .AnyAsync(item => item.Id == userId, cancellationToken))
        {
            return null;
        }

        var profile = await _db.UserProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var availableAt = profile?.NicknameChangedAt
            + UserProfilePolicy.NicknameChangeInterval;

        return new UserProfileEditState(
            profile?.Nickname,
            profile?.Biography ?? string.Empty,
            UserProfilePolicy.CanChangeNickname(profile?.NicknameChangedAt, now),
            availableAt);
    }

    public async Task<UserProfileUpdateResult> UpdateAsync(
        long userId,
        string? nicknameInput,
        string? biographyInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var violations = new List<ValidationViolation>();
        NicknameValue? desiredNickname = null;
        if (!string.IsNullOrWhiteSpace(nicknameInput)
            && !UserProfilePolicy.TryCreateNickname(
                nicknameInput,
                out desiredNickname,
                out var nicknameError))
        {
            violations.Add(new ValidationViolation(
                NicknameField,
                GetNicknameError(nicknameError)));
        }

        if (!UserProfilePolicy.TryNormalizeBiography(
                biographyInput,
                out var biography,
                out _))
        {
            violations.Add(new ValidationViolation(
                BiographyField,
                $"소개는 {UserProfilePolicy.BiographyMaxLength}자 이하여야 합니다."));
        }

        if (violations.Count > 0)
        {
            return new UserProfileUpdateResult(
                UserProfileUpdateOutcome.Invalid,
                violations);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var users = await _db.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var user = users.SingleOrDefault();
        if (user is null
            || !AccountStatusPolicy.AllowsMutation(
                user.AccountStatus,
                user.SuspendedUntil))
        {
            await transaction.CommitAsync(cancellationToken);
            return new UserProfileUpdateResult(
                UserProfileUpdateOutcome.AccountUnavailable,
                NoViolations);
        }

        var profile = await _db.UserProfiles
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var nicknameChanged = !string.Equals(
            profile?.Nickname,
            desiredNickname?.Value,
            StringComparison.Ordinal);
        var now = _timeProvider.GetUtcNow();
        if (nicknameChanged
            && !UserProfilePolicy.CanChangeNickname(profile?.NicknameChangedAt, now))
        {
            var availableAt = profile!.NicknameChangedAt
                + UserProfilePolicy.NicknameChangeInterval;
            await transaction.CommitAsync(cancellationToken);
            return new UserProfileUpdateResult(
                UserProfileUpdateOutcome.NicknameChangeTooSoon,
                [new ValidationViolation(
                    NicknameField,
                    "닉네임은 7일마다 변경하거나 해제할 수 있습니다.")],
                availableAt);
        }

        if (nicknameChanged
            && desiredNickname is not null
            && await _db.UserProfiles.AnyAsync(
                item => item.UserId != userId
                    && item.NormalizedNickname == desiredNickname.UniquenessKey,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return NicknameUnavailable();
        }

        var biographyChanged = !string.Equals(
            profile?.Biography ?? string.Empty,
            biography,
            StringComparison.Ordinal);
        if (profile is null)
        {
            if (!nicknameChanged && !biographyChanged)
            {
                await transaction.CommitAsync(cancellationToken);
                return Updated();
            }

            profile = new UserProfile
            {
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.UserProfiles.Add(profile);
        }
        else if (!nicknameChanged && !biographyChanged)
        {
            await transaction.CommitAsync(cancellationToken);
            return Updated();
        }

        profile.Nickname = desiredNickname?.Value;
        profile.NormalizedNickname = desiredNickname?.UniquenessKey;
        profile.Biography = biography;
        profile.UpdatedAt = now;
        if (nicknameChanged)
        {
            profile.NicknameChangedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Updated();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: UniqueViolation,
                ConstraintName: NormalizedNicknameIndex,
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return NicknameUnavailable();
        }
    }

    private static UserProfileUpdateResult Updated() =>
        new(UserProfileUpdateOutcome.Updated, NoViolations);

    private static UserProfileUpdateResult NicknameUnavailable() =>
        new(
            UserProfileUpdateOutcome.NicknameUnavailable,
            [new ValidationViolation(
                NicknameField,
                "이미 사용 중인 닉네임입니다.")]);

    private static string GetNicknameError(NicknameValidationError error) => error switch
    {
        NicknameValidationError.TooShort =>
            $"닉네임은 {UserProfilePolicy.NicknameMinLength}자 이상이어야 합니다.",
        NicknameValidationError.TooLong =>
            $"닉네임은 {UserProfilePolicy.NicknameMaxLength}자 이하여야 합니다.",
        NicknameValidationError.InvalidCharacter =>
            "닉네임에는 한글, 영문, 숫자, 공백, 밑줄과 하이픈만 사용할 수 있습니다.",
        _ => "닉네임을 입력해 주세요.",
    };
}
