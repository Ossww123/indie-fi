using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropEligibilityServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropEligibilityServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EvaluateAsync_WithNoRequirements_AllowsActiveAccount()
    {
        var arranged = await CreateDropAndUserAsync(null, null);

        await using var db = _fixture.NewDbContext();
        var outcome = await new KeyDropEligibilityService(db).EvaluateAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(KeyDropEligibilityOutcome.Eligible, outcome);
    }

    [Fact]
    public async Task EvaluateAsync_WithAgeAndReviewRequirements_RequiresBothCurrentValues()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        var arranged = await CreateDropForUserAsync(
            assignment.ReviewerId,
            minimumAccountAgeDays: 7,
            minimumCompletedReviewCount: 1);

        await using (var arrangeDb = _fixture.NewOwnerDbContext())
        {
            await arrangeDb.Users
                .Where(user => user.Id == assignment.ReviewerId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        user => user.CreatedAt,
                        DateTimeOffset.UtcNow.AddDays(-8)),
                    TestContext.Current.CancellationToken);
        }

        KeyDropEligibilityOutcome beforeCompletion;
        await using (var beforeDb = _fixture.NewDbContext())
        {
            beforeCompletion = await new KeyDropEligibilityService(beforeDb)
                .EvaluateAsync(
                    arranged.DropId,
                    assignment.ReviewerId,
                    TestContext.Current.CancellationToken);
        }

        await using (var completionDb = _fixture.NewDbContext())
        {
            await completionDb.ReviewAssignments
                .Where(item => item.Id == assignment.AssignmentId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            item => item.Status,
                            ReviewAssignmentStatus.Completed)
                        .SetProperty(
                            item => item.CompletedAt,
                            DateTimeOffset.UtcNow)
                        .SetProperty(
                            item => item.ExpiredAt,
                            (DateTimeOffset?)null),
                    TestContext.Current.CancellationToken);
        }

        await using var afterDb = _fixture.NewDbContext();
        var afterCompletion = await new KeyDropEligibilityService(afterDb)
            .EvaluateAsync(
                arranged.DropId,
                assignment.ReviewerId,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            KeyDropEligibilityOutcome.NotEnoughCompletedReviews,
            beforeCompletion);
        Assert.Equal(KeyDropEligibilityOutcome.Eligible, afterCompletion);
    }

    [Fact]
    public async Task EvaluateAsync_WhenAccountIsSuspended_ReturnsAccountUnavailable()
    {
        var arranged = await CreateDropAndUserAsync(null, null);
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.Users
                .Where(user => user.Id == arranged.UserId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        user => user.AccountStatus,
                        AccountStatus.IndefinitelySuspended),
                    TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var outcome = await new KeyDropEligibilityService(db).EvaluateAsync(
            arranged.DropId,
            arranged.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(KeyDropEligibilityOutcome.AccountUnavailable, outcome);
    }

    private async Task<(long DropId, long UserId)> CreateDropAndUserAsync(
        int? minimumAccountAgeDays,
        int? minimumCompletedReviewCount)
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var drop = await CreateDropAsync(
            db,
            minimumAccountAgeDays,
            minimumCompletedReviewCount);
        return (drop.Id, user.Id);
    }

    private async Task<(long DropId, long UserId)> CreateDropForUserAsync(
        long userId,
        int? minimumAccountAgeDays,
        int? minimumCompletedReviewCount)
    {
        await using var db = _fixture.NewDbContext();
        var drop = await CreateDropAsync(
            db,
            minimumAccountAgeDays,
            minimumCompletedReviewCount);
        return (drop.Id, userId);
    }

    private static async Task<KeyDrop> CreateDropAsync(
        IndieFi.Web.Data.AppDbContext db,
        int? minimumAccountAgeDays,
        int? minimumCompletedReviewCount)
    {
        var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(1),
            TotalKeys = 1,
            MinimumAccountAgeDays = minimumAccountAgeDays,
            MinimumCompletedReviewCount = minimumCompletedReviewCount,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return drop;
    }
}
