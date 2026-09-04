using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserFollowPersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public UserFollowPersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Save_ValidFollow_PersistsRelationshipAndCreatedTime()
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.UserFollows.Add(new UserFollow
            {
                FollowerId = followerId,
                FollowedUserId = followedUserId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var follow = await verificationDb.UserFollows.SingleAsync(
            item => item.FollowerId == followerId
                && item.FollowedUserId == followedUserId,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(default, follow.CreatedAt);
    }

    [Fact]
    public async Task Save_DuplicateFollow_IsRejectedByCompositePrimaryKey()
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.UserFollows.Add(new UserFollow
            {
                FollowerId = followerId,
                FollowedUserId = followedUserId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicateDb = _fixture.NewDbContext();
        duplicateDb.UserFollows.Add(new UserFollow
        {
            FollowerId = followerId,
            FollowedUserId = followedUserId,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicateDb.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Save_SelfFollow_IsRejectedByDatabaseConstraint()
    {
        var (userId, _) = await CreateTwoUsersAsync();
        await using var db = _fixture.NewDbContext();
        db.UserFollows.Add(new UserFollow
        {
            FollowerId = userId,
            FollowedUserId = userId,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task DeleteUser_CascadesFollowRelationships()
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using (var db = _fixture.NewDbContext())
        {
            db.UserFollows.Add(new UserFollow
            {
                FollowerId = followerId,
                FollowedUserId = followedUserId,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            await ownerDb.Users
                .Where(user => user.Id == followedUserId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.UserFollows.AnyAsync(
            item => item.FollowerId == followerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplicationRole_CannotRewriteFollowHistory()
    {
        var (followerId, followedUserId) = await CreateTwoUsersAsync();
        await using var db = _fixture.NewDbContext();
        db.UserFollows.Add(new UserFollow
        {
            FollowerId = followerId,
            FollowedUserId = followedUserId,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.UserFollows
                .Where(item => item.FollowerId == followerId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.CreatedAt,
                        DateTimeOffset.UtcNow.AddDays(-1)),
                    TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private async Task<(long FirstUserId, long SecondUserId)> CreateTwoUsersAsync()
    {
        await using var db = _fixture.NewDbContext();
        var first = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var second = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        return (first.Id, second.Id);
    }
}
