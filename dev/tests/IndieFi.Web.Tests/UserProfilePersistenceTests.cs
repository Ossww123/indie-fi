using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserProfilePersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public UserProfilePersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApplicationRole_CanStoreAndReadProfile()
    {
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = (await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0)).Id;
            db.UserProfiles.Add(CreateProfile(userId, "Alpha 게이머", "ALPHA 게이머"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var profile = await verificationDb.UserProfiles
            .SingleAsync(
                item => item.UserId == userId,
                TestContext.Current.CancellationToken);

        Assert.Equal("Alpha 게이머", profile.Nickname);
        Assert.Equal("게임 개발과 리뷰를 좋아합니다.", profile.Biography);
    }

    [Fact]
    public async Task Insert_WhenNormalizedNicknameAlreadyExists_IsRejectedByDatabase()
    {
        await using var db = _fixture.NewDbContext();
        var firstUser = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var secondUser = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        db.UserProfiles.Add(CreateProfile(firstUser.Id, "UniquePlayer", "UNIQUEPLAYER"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.UserProfiles.Add(CreateProfile(secondUser.Id, "uniqueplayer", "UNIQUEPLAYER"));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_WhenNicknameAndNormalizedValueDoNotAppearTogether_IsRejectedByDatabase()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var profile = CreateProfile(user.Id, "ShapePlayer", "SHAPEPLAYER");
        profile.NormalizedNickname = null;
        db.UserProfiles.Add(profile);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Insert_WhenBiographyExceedsLimit_IsRejectedByDatabase()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var profile = CreateProfile(user.Id, "LongBiography", "LONGBIOGRAPHY");
        profile.Biography = new string('가', UserProfilePolicy.BiographyMaxLength + 1);
        db.UserProfiles.Add(profile);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static UserProfile CreateProfile(
        long userId,
        string nickname,
        string normalizedNickname)
    {
        var now = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        return new UserProfile
        {
            UserId = userId,
            Nickname = nickname,
            NormalizedNickname = normalizedNickname,
            Biography = "게임 개발과 리뷰를 좋아합니다.",
            NicknameChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
