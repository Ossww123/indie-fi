using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserDisplayNameResolverTests
{
    private readonly PostgreSqlFixture _fixture;

    public UserDisplayNameResolverTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ResolveMany_ReturnsNicknameAndFallbackInOneQuery()
    {
        long nicknamedUserId;
        long fallbackUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var nicknamedUser = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0);
            var fallbackUser = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0);
            nicknamedUserId = nicknamedUser.Id;
            fallbackUserId = fallbackUser.Id;
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            arrangeDb.UserProfiles.Add(new UserProfile
            {
                UserId = nicknamedUserId,
                Nickname = "ResolverPlayer",
                NormalizedNickname = "RESOLVERPLAYER",
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var roundTrips = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(roundTrips);
        var names = await new UserDisplayNameResolver(db).ResolveAsync(
            [nicknamedUserId, fallbackUserId, nicknamedUserId],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, names.Count);
        Assert.Equal("ResolverPlayer", names[nicknamedUserId]);
        Assert.Equal($"user#{fallbackUserId}", names[fallbackUserId]);
        Assert.Equal(1, roundTrips.Count);
    }

    [Fact]
    public async Task ResolveMany_WithNoUsers_DoesNotQueryDatabase()
    {
        var roundTrips = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(roundTrips);

        var names = await new UserDisplayNameResolver(db).ResolveAsync(
            [],
            TestContext.Current.CancellationToken);

        Assert.Empty(names);
        Assert.Equal(0, roundTrips.Count);
    }
}
