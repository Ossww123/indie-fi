using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountSessionValidationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public AccountSessionValidationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IsValidAsync_WithCurrentActiveAccount_UsesOneQueryAndReturnsTrue()
    {
        var userId = await CreateUserAsync(
            emailConfirmed: true,
            AccountStatus.Active,
            sessionVersion: 7);
        var roundTrips = new RoundTripCountingInterceptor();

        await using var db = _fixture.NewDbContext(roundTrips);
        var isValid = await new AccountSessionValidationService(db).IsValidAsync(
            userId,
            sessionVersion: 7,
            TestContext.Current.CancellationToken);

        Assert.True(isValid);
        Assert.Equal(1, roundTrips.Count);
    }

    [Fact]
    public async Task ValidateAsync_WithNickname_ReturnsCurrentDisplayNameInOneQuery()
    {
        var userId = await CreateUserAsync(
            emailConfirmed: true,
            AccountStatus.Active,
            sessionVersion: 9);
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            arrangeDb.UserProfiles.Add(new UserProfile
            {
                UserId = userId,
                Nickname = "SessionPlayer",
                NormalizedNickname = "SESSIONPLAYER",
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var roundTrips = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(roundTrips);
        var result = await new AccountSessionValidationService(db).ValidateAsync(
            userId,
            sessionVersion: 9,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("SessionPlayer", result.DisplayName);
        Assert.Equal(1, roundTrips.Count);
    }

    [Fact]
    public async Task IsValidAsync_WithDifferentSessionVersion_ReturnsFalse()
    {
        var userId = await CreateUserAsync(
            emailConfirmed: true,
            AccountStatus.Active,
            sessionVersion: 3);

        await using var db = _fixture.NewDbContext();
        var isValid = await new AccountSessionValidationService(db).IsValidAsync(
            userId,
            sessionVersion: 2,
            TestContext.Current.CancellationToken);

        Assert.False(isValid);
    }

    [Theory]
    [InlineData(false, AccountStatus.Active)]
    [InlineData(true, AccountStatus.TemporarilySuspended)]
    [InlineData(true, AccountStatus.IndefinitelySuspended)]
    public async Task IsValidAsync_WithUnavailableAccount_ReturnsFalse(
        bool emailConfirmed,
        AccountStatus accountStatus)
    {
        var userId = await CreateUserAsync(
            emailConfirmed,
            accountStatus,
            sessionVersion: 1);

        await using var db = _fixture.NewDbContext();
        var isValid = await new AccountSessionValidationService(db).IsValidAsync(
            userId,
            sessionVersion: 1,
            TestContext.Current.CancellationToken);

        Assert.False(isValid);
    }

    [Fact]
    public async Task IsValidAsync_WithUnknownUser_ReturnsFalse()
    {
        await using var db = _fixture.NewDbContext();
        var isValid = await new AccountSessionValidationService(db).IsValidAsync(
            long.MaxValue,
            sessionVersion: 1,
            TestContext.Current.CancellationToken);

        Assert.False(isValid);
    }

    private async Task<long> CreateUserAsync(
        bool emailConfirmed,
        AccountStatus accountStatus,
        long sessionVersion)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"session-validation-{Guid.NewGuid():N}@example.test");
        user.EmailConfirmedAt = emailConfirmed ? DateTimeOffset.UtcNow : null;
        user.AccountStatus = accountStatus;
        user.SuspendedUntil = accountStatus == AccountStatus.TemporarilySuspended
            ? DateTimeOffset.UtcNow.AddHours(1)
            : null;
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }
}
