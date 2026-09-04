using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserFollowServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public UserFollowServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Follow_ThenRepeat_CreatesOneRelationshipAndReturnsCurrentState()
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using var db = _fixture.NewDbContext();
        var service = new UserFollowService(db);

        var first = await service.FollowAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken);
        var repeated = await service.FollowAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(UserFollowOutcome.Following, first.Outcome);
        Assert.True(first.Changed);
        Assert.Equal(UserFollowOutcome.Following, repeated.Outcome);
        Assert.False(repeated.Changed);
        Assert.True(await service.IsFollowingAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.UserFollows.CountAsync(
            item => item.FollowerId == followerId
                && item.FollowedUserId == followedUserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unfollow_ThenRepeat_RemovesRelationshipIdempotently()
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using var db = _fixture.NewDbContext();
        var service = new UserFollowService(db);
        await service.FollowAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken);

        var first = await service.UnfollowAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken);
        var repeated = await service.UnfollowAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(UserFollowOutcome.NotFollowing, first.Outcome);
        Assert.True(first.Changed);
        Assert.Equal(UserFollowOutcome.NotFollowing, repeated.Outcome);
        Assert.False(repeated.Changed);
        Assert.False(await service.IsFollowingAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Follow_Self_IsRejectedWithoutCreatingRelationship()
    {
        var (userId, _) = await CreateTwoUsersAsync();
        await using var db = _fixture.NewDbContext();

        var result = await new UserFollowService(db).FollowAsync(
            userId,
            userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(UserFollowOutcome.CannotFollowSelf, result.Outcome);
        Assert.False(result.Changed);
        Assert.False(await db.UserFollows.AnyAsync(
            item => item.FollowerId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Follow_MissingTarget_ReturnsNotFoundWithoutCreatingRelationship()
    {
        var (followerId, _) = await CreateTwoUsersAsync();
        await using var db = _fixture.NewDbContext();

        var result = await new UserFollowService(db).FollowAsync(
            followerId,
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(UserFollowOutcome.TargetNotFound, result.Outcome);
        Assert.False(result.Changed);
        Assert.False(await db.UserFollows.AnyAsync(
            item => item.FollowerId == followerId,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(AccountStatus.TemporarilySuspended)]
    [InlineData(AccountStatus.IndefinitelySuspended)]
    public async Task Follow_UnavailableFollower_DoesNotChangeRelationship(
        AccountStatus accountStatus)
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var follower = await arrangeDb.Users.SingleAsync(
                user => user.Id == followerId,
                TestContext.Current.CancellationToken);
            follower.AccountStatus = accountStatus;
            follower.SuspendedUntil = accountStatus == AccountStatus.TemporarilySuspended
                ? DateTimeOffset.UtcNow.AddHours(1)
                : null;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var result = await new UserFollowService(db).FollowAsync(
            followerId,
            followedUserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(UserFollowOutcome.AccountUnavailable, result.Outcome);
        Assert.False(await db.UserFollows.AnyAsync(
            item => item.FollowerId == followerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Follow_ConcurrentRepeats_ConvergeToOneRelationship()
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();

        var results = await Task.WhenAll(
            new UserFollowService(firstDb).FollowAsync(
                followerId,
                followedUserId,
                TestContext.Current.CancellationToken),
            new UserFollowService(secondDb).FollowAsync(
                followerId,
                followedUserId,
                TestContext.Current.CancellationToken));

        Assert.All(results, result =>
            Assert.Equal(UserFollowOutcome.Following, result.Outcome));
        Assert.Single(results, result => result.Changed);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.UserFollows.CountAsync(
            item => item.FollowerId == followerId
                && item.FollowedUserId == followedUserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetFollowing_ReturnsNewestFirstWithNicknameAndFallback()
    {
        var (followerId, firstTargetId) = await CreateTwoUsersAsync();
        long secondTargetId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            secondTargetId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            arrangeDb.UserProfiles.Add(new UserProfile
            {
                UserId = firstTargetId,
                Nickname = "FollowedPlayer",
                NormalizedNickname = "FOLLOWEDPLAYER",
                Biography = "팔로우한 사용자 소개",
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var service = new UserFollowService(db);
        await service.FollowAsync(
            followerId,
            firstTargetId,
            TestContext.Current.CancellationToken);
        await service.FollowAsync(
            followerId,
            secondTargetId,
            TestContext.Current.CancellationToken);

        var items = await service.GetFollowingAsync(
            followerId,
            TestContext.Current.CancellationToken);

        Assert.Equal([secondTargetId, firstTargetId], items.Select(item => item.UserId));
        Assert.Equal($"user#{secondTargetId}", items[0].DisplayName);
        Assert.Equal("FollowedPlayer", items[1].DisplayName);
        Assert.Equal("팔로우한 사용자 소개", items[1].Biography);
    }

    private async Task<(long FirstUserId, long SecondUserId)> CreateTwoUsersAsync()
    {
        await using var db = _fixture.NewDbContext();
        var first = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var second = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        return (first.Id, second.Id);
    }
}
