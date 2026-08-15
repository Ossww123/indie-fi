using System.Data.Common;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserProfileManagementServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public UserProfileManagementServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetForEdit_WhenProfileDoesNotExist_ReturnsEmptyEditableState()
    {
        var userId = await CreateUserAsync();

        await using var db = _fixture.NewDbContext();
        var state = await NewService(db, Now).GetForEditAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(state);
        Assert.Null(state.Nickname);
        Assert.Empty(state.Biography);
        Assert.True(state.CanChangeNickname);
        Assert.Null(state.NicknameChangeAvailableAt);
    }

    [Fact]
    public async Task GetForEdit_WhenAccountDoesNotExist_ReturnsNull()
    {
        await using var db = _fixture.NewDbContext();

        var state = await NewService(db, Now).GetForEditAsync(
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Null(state);
    }

    [Fact]
    public async Task Update_FirstProfile_NormalizesAndStoresPublicValues()
    {
        var userId = await CreateUserAsync();
        var suffix = $"{Guid.NewGuid():N}"[..6];
        await using (var db = _fixture.NewDbContext())
        {
            var result = await NewService(db, Now).UpdateAsync(
                userId,
                $"  Ａｌｐｈａ{suffix}\t 게이머  ",
                "  첫 줄\r\n둘째 줄  ",
                TestContext.Current.CancellationToken);

            Assert.Equal(UserProfileUpdateOutcome.Updated, result.Outcome);
            Assert.Empty(result.Violations);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var profile = await verificationDb.UserProfiles.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal($"Alpha{suffix} 게이머", profile.Nickname);
        Assert.Equal($"ALPHA{suffix.ToUpperInvariant()} 게이머", profile.NormalizedNickname);
        Assert.Equal("첫 줄\n둘째 줄", profile.Biography);
        Assert.Equal(Now, profile.NicknameChangedAt);
        Assert.Equal(Now, profile.CreatedAt);
        Assert.Equal(Now, profile.UpdatedAt);
    }

    [Fact]
    public async Task Update_WithInvalidNicknameAndBiography_ReturnsEveryViolation()
    {
        var userId = await CreateUserAsync();
        await using var db = _fixture.NewDbContext();

        var result = await NewService(db, Now).UpdateAsync(
            userId,
            "player!",
            new string('가', UserProfilePolicy.BiographyMaxLength + 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(UserProfileUpdateOutcome.Invalid, result.Outcome);
        Assert.Equal(2, result.Violations.Count);
        Assert.Contains(
            result.Violations,
            item => item.Field == UserProfileManagementService.NicknameField);
        Assert.Contains(
            result.Violations,
            item => item.Field == UserProfileManagementService.BiographyField);
        Assert.False(await db.UserProfiles.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_WhenNicknameIsAlreadyUsed_ReturnsFriendlyConflict()
    {
        var firstUserId = await CreateUserAsync();
        var secondUserId = await CreateUserAsync();
        await UpdateAsync(firstUserId, Now, "CasePlayer", "첫 번째");

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, Now).UpdateAsync(
            secondUserId,
            "caseplayer",
            "두 번째",
            TestContext.Current.CancellationToken);

        Assert.Equal(UserProfileUpdateOutcome.NicknameUnavailable, result.Outcome);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(UserProfileManagementService.NicknameField, violation.Field);
        Assert.False(await db.UserProfiles.AnyAsync(
            item => item.UserId == secondUserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Update_NicknameBeforeSevenDays_IsRejectedAtExactBoundaryOnly()
    {
        var userId = await CreateUserAsync();
        await UpdateAsync(userId, Now, "FirstName", string.Empty);

        await using (var earlyDb = _fixture.NewDbContext())
        {
            var early = await NewService(earlyDb, Now.AddDays(7).AddTicks(-1))
                .UpdateAsync(
                    userId,
                    "SecondName",
                    string.Empty,
                    TestContext.Current.CancellationToken);

            Assert.Equal(UserProfileUpdateOutcome.NicknameChangeTooSoon, early.Outcome);
            Assert.Equal(Now.AddDays(7), early.NicknameChangeAvailableAt);
        }

        await using (var boundaryDb = _fixture.NewDbContext())
        {
            var boundary = await NewService(boundaryDb, Now.AddDays(7)).UpdateAsync(
                userId,
                "SecondName",
                string.Empty,
                TestContext.Current.CancellationToken);

            Assert.Equal(UserProfileUpdateOutcome.Updated, boundary.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var profile = await verificationDb.UserProfiles.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal("SecondName", profile.Nickname);
        Assert.Equal(Now.AddDays(7), profile.NicknameChangedAt);
    }

    [Fact]
    public async Task Update_BiographyOnly_DoesNotRestartNicknameCooldown()
    {
        var userId = await CreateUserAsync();
        await UpdateAsync(userId, Now, "StableName", "기존 소개");

        await using (var db = _fixture.NewDbContext())
        {
            var result = await NewService(db, Now.AddDays(1)).UpdateAsync(
                userId,
                "StableName",
                "새 소개",
                TestContext.Current.CancellationToken);

            Assert.Equal(UserProfileUpdateOutcome.Updated, result.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var profile = await verificationDb.UserProfiles.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal("새 소개", profile.Biography);
        Assert.Equal(Now, profile.NicknameChangedAt);
        Assert.Equal(Now.AddDays(1), profile.UpdatedAt);
    }

    [Fact]
    public async Task Update_ClearingNickname_StartsAnotherSevenDayCooldown()
    {
        var userId = await CreateUserAsync();
        await UpdateAsync(userId, Now, "Clearable", string.Empty);
        await UpdateAsync(userId, Now.AddDays(7), string.Empty, string.Empty);

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, Now.AddDays(13)).UpdateAsync(
            userId,
            "NewName",
            string.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(UserProfileUpdateOutcome.NicknameChangeTooSoon, result.Outcome);
        var profile = await db.UserProfiles.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Null(profile.Nickname);
        Assert.Null(profile.NormalizedNickname);
        Assert.Equal(Now.AddDays(7), profile.NicknameChangedAt);
    }

    [Fact]
    public async Task Update_SuspendedAccount_DoesNotCreateProfile()
    {
        var userId = await CreateUserAsync();
        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            var user = await ownerDb.Users.SingleAsync(
                item => item.Id == userId,
                TestContext.Current.CancellationToken);
            user.AccountStatus = AccountStatus.IndefinitelySuspended;
            await ownerDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, Now).UpdateAsync(
            userId,
            "BlockedName",
            "저장되면 안 됨",
            TestContext.Current.CancellationToken);

        Assert.Equal(UserProfileUpdateOutcome.AccountUnavailable, result.Outcome);
        Assert.False(await db.UserProfiles.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentUsers_RequestSameNickname_OneFriendlyResultWins()
    {
        var firstUserId = await CreateUserAsync();
        var secondUserId = await CreateUserAsync();
        var nickname = $"Same{Guid.NewGuid():N}"[..18];

        var barrier = new NicknameAvailabilityBarrier();
        await using var firstDb = _fixture.NewDbContext(barrier);
        await using var secondDb = _fixture.NewDbContext(barrier);
        var results = await Task.WhenAll(
            NewService(firstDb, Now).UpdateAsync(
                firstUserId,
                nickname,
                string.Empty,
                TestContext.Current.CancellationToken),
            NewService(secondDb, Now).UpdateAsync(
                secondUserId,
                nickname.ToUpperInvariant(),
                string.Empty,
                TestContext.Current.CancellationToken));

        Assert.Single(
            results,
            item => item.Outcome == UserProfileUpdateOutcome.Updated);
        Assert.Single(
            results,
            item => item.Outcome == UserProfileUpdateOutcome.NicknameUnavailable);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            1,
            await verificationDb.UserProfiles.CountAsync(
                item => item.NormalizedNickname == nickname.ToUpperInvariant(),
                TestContext.Current.CancellationToken));
    }

    private async Task<long> CreateUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        return (await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0)).Id;
    }

    private async Task<UserProfileUpdateResult> UpdateAsync(
        long userId,
        DateTimeOffset now,
        string? nickname,
        string? biography)
    {
        await using var db = _fixture.NewDbContext();
        return await NewService(db, now).UpdateAsync(
            userId,
            nickname,
            biography,
            TestContext.Current.CancellationToken);
    }

    private static UserProfileManagementService NewService(
        Data.AppDbContext db,
        DateTimeOffset now) =>
        new(db, new FixedTimeProvider(now));

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class NicknameAvailabilityBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _bothQueriesReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "SELECT EXISTS",
                    StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(
                    "normalized_nickname",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref _arrivalCount) == 2)
                {
                    _bothQueriesReached.TrySetResult(true);
                }

                await _bothQueriesReached.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
