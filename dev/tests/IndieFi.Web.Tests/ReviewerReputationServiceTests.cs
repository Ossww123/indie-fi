using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewerReputationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReviewerReputationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Calculate_UsesSmoothedQualityAndReliabilityScores()
    {
        var result = ReviewerReputationPolicy.Calculate(
            completedReviewCount: 5,
            expiredAssignmentCount: 2,
            ratingCount: 3,
            ratingScoreSum: 12);

        Assert.Equal(59.375m, result.QualityScore);
        Assert.InRange(result.CompletionReliabilityScore, 66.666666m, 66.666667m);
        Assert.InRange(result.TrustScore, 61.562499m, 61.562501m);
        Assert.Equal(ReviewerGrade.Silver, result.Grade);

        var insufficientSample = ReviewerReputationPolicy.Calculate(
            completedReviewCount: 4,
            expiredAssignmentCount: 0,
            ratingCount: 3,
            ratingScoreSum: 15);
        Assert.Equal(ReviewerGrade.Unranked, insufficientSample.Grade);
    }

    [Theory]
    [InlineData("54.999", ReviewerGrade.Bronze)]
    [InlineData("55", ReviewerGrade.Silver)]
    [InlineData("69.999", ReviewerGrade.Silver)]
    [InlineData("70", ReviewerGrade.Gold)]
    [InlineData("84.999", ReviewerGrade.Gold)]
    [InlineData("85", ReviewerGrade.Platinum)]
    public void DetermineGrade_UsesUnroundedBoundaryScore(
        string trustScoreText,
        ReviewerGrade expected)
    {
        var trustScore = decimal.Parse(
            trustScoreText,
            System.Globalization.CultureInfo.InvariantCulture);

        var grade = ReviewerReputationPolicy.DetermineGrade(
            completedReviewCount: 5,
            ratingCount: 3,
            trustScore);

        Assert.Equal(expected, grade);
    }

    [Fact]
    public async Task RecalculateAsync_RebuildsSourceCountsAndDoesNotReviseUnchangedProjection()
    {
        var history = await CreateHistoryAsync(
            completedCount: 5,
            expiredCount: 2,
            ratingScores: [5, 4, 3]);
        await using (var corruptDb = _fixture.NewDbContext())
        {
            await corruptDb.ReviewerReputations
                .Where(item => item.ReviewerId == history.ReviewerId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.CompletedReviewCount, 0)
                        .SetProperty(item => item.ExpiredAssignmentCount, 0)
                        .SetProperty(item => item.RatingCount, 0)
                        .SetProperty(item => item.RatingScoreSum, 0),
                    TestContext.Current.CancellationToken);
        }

        ReviewerReputationSnapshot first;
        ReviewerReputationSnapshot second;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            var service = NewService(serviceDb);
            first = Assert.IsType<ReviewerReputationSnapshot>(
                await service.RecalculateAsync(
                    history.ReviewerId,
                    TestContext.Current.CancellationToken));
            second = Assert.IsType<ReviewerReputationSnapshot>(
                await service.RecalculateAsync(
                    history.ReviewerId,
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(5, first.CompletedReviewCount);
        Assert.Equal(2, first.ExpiredAssignmentCount);
        Assert.Equal(3, first.RatingCount);
        Assert.Equal(12, first.RatingScoreSum);
        Assert.Equal(ReviewerGrade.Silver, first.Grade);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.UpdatedAt, second.UpdatedAt);
    }

    [Fact]
    public async Task RecalculateAllAsync_ZeroesExistingProjectionWithoutSourceHistory()
    {
        long reviewerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            arrangeDb.ReviewerReputations.Add(new ReviewerReputation
            {
                ReviewerId = reviewerId,
                CompletedReviewCount = 2,
                ExpiredAssignmentCount = 1,
                RatingCount = 1,
                RatingScoreSum = 3,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int processedCount;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            processedCount = await NewService(serviceDb).RecalculateAllAsync(
                TestContext.Current.CancellationToken);
        }

        Assert.True(processedCount >= 1);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.ReviewerReputations
            .AsNoTracking()
            .SingleAsync(
                item => item.ReviewerId == reviewerId,
                TestContext.Current.CancellationToken);
        Assert.Equal(0, stored.CompletedReviewCount);
        Assert.Equal(0, stored.ExpiredAssignmentCount);
        Assert.Equal(0, stored.RatingCount);
        Assert.Equal(0, stored.RatingScoreSum);
    }

    [Fact]
    public async Task SourceChanges_RefreshProjectionWithoutExplicitRecalculation()
    {
        var history = await CreateHistoryAsync(
            completedCount: 2,
            expiredCount: 1,
            ratingScores: [2, 4]);

        var initial = await ReadStoredAsync(history.ReviewerId);
        Assert.Equal(2, initial.CompletedReviewCount);
        Assert.Equal(1, initial.ExpiredAssignmentCount);
        Assert.Equal(2, initial.RatingCount);
        Assert.Equal(6, initial.RatingScoreSum);

        await using (var updateDb = _fixture.NewDbContext())
        {
            await updateDb.FeedbackRatings
                .Where(rating => rating.Id == history.RatingIds[0])
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(rating => rating.Score, 5)
                        .SetProperty(rating => rating.Revision, rating => rating.Revision + 1)
                        .SetProperty(rating => rating.UpdatedAt, DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        var updated = await ReadStoredAsync(history.ReviewerId);
        Assert.Equal(9, updated.RatingScoreSum);
        Assert.True(updated.Revision > initial.Revision);

        await using (var deletionDb = _fixture.NewOwnerDbContext())
        {
            await deletionDb.FeedbackRatings
                .Where(rating => rating.Id == history.RatingIds[0])
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var afterDeletion = await ReadStoredAsync(history.ReviewerId);
        Assert.Equal(1, afterDeletion.RatingCount);
        Assert.Equal(4, afterDeletion.RatingScoreSum);
        Assert.True(afterDeletion.Revision > updated.Revision);
    }

    [Fact]
    public async Task ConcurrentTerminalTransitions_ConvergeToExactReviewerCounts()
    {
        var active = await CreateActiveAssignmentsAsync(count: 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> ExpireAsync(long assignmentId) => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = _fixture.NewDbContext();
            return await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_assignments
                   SET status = 'Expired', expired_at = now()
                 WHERE id = @assignmentId
                   AND status = 'Active'
                """,
                [new NpgsqlParameter("assignmentId", assignmentId)],
                TestContext.Current.CancellationToken);
        });

        var first = ExpireAsync(active.AssignmentIds[0]);
        var second = ExpireAsync(active.AssignmentIds[1]);
        gate.SetResult();

        Assert.All(await Task.WhenAll(first, second), count => Assert.Equal(1, count));
        var stored = await ReadStoredAsync(active.ReviewerId);
        Assert.Equal(0, stored.CompletedReviewCount);
        Assert.Equal(2, stored.ExpiredAssignmentCount);
    }

    private async Task<ReviewerHistory> CreateHistoryAsync(
        int completedCount,
        int expiredCount,
        IReadOnlyList<int> ratingScores)
    {
        await using var db = _fixture.NewOwnerDbContext();
        var reviewer = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var ratingIds = new List<long>();

        for (var index = 0; index < completedCount; index++)
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
            var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
            var request = await ReviewTestData.CreateQueuedRequestAsync(db, owner.Id, game.Id);
            var completedAt = DateTimeOffset.UtcNow;
            request.Status = ReviewRequestStatus.Completed;
            request.CompletedAt = completedAt;
            var assignment = new ReviewAssignment
            {
                RequestId = request.Id,
                GameId = game.Id,
                ReviewerId = reviewer.Id,
                ClaimOperationId = Guid.NewGuid(),
                Status = ReviewAssignmentStatus.Completed,
                LeasedUntil = completedAt.AddHours(1),
                CompletedAt = completedAt,
            };
            db.ReviewAssignments.Add(assignment);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var earnEntry = new CreditLedgerEntry
            {
                UserId = reviewer.Id,
                Kind = CreditEntryKind.Earn,
                Delta = 1,
            };
            var submission = new FeedbackSubmission
            {
                AssignmentId = assignment.Id,
                ReviewerId = reviewer.Id,
                EarnEntry = earnEntry,
                Body = $"집계 테스트를 위한 구체적이고 충분히 긴 피드백 본문 {Guid.NewGuid():N}",
            };
            db.CreditLedgerEntries.Add(earnEntry);
            db.FeedbackSubmissions.Add(submission);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            if (index < ratingScores.Count)
            {
                var rating = new FeedbackRating
                {
                    FeedbackSubmissionId = submission.Id,
                    RaterId = owner.Id,
                    ReviewerId = reviewer.Id,
                    Score = ratingScores[index],
                };
                db.FeedbackRatings.Add(rating);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                ratingIds.Add(rating.Id);
            }
        }

        for (var index = 0; index < expiredCount; index++)
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
            var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
            var request = await ReviewTestData.CreateQueuedRequestAsync(db, owner.Id, game.Id);
            var expiredAt = DateTimeOffset.UtcNow;
            db.ReviewAssignments.Add(new ReviewAssignment
            {
                RequestId = request.Id,
                GameId = game.Id,
                ReviewerId = reviewer.Id,
                ClaimOperationId = Guid.NewGuid(),
                Status = ReviewAssignmentStatus.Expired,
                LeasedUntil = expiredAt.AddMinutes(-1),
                ExpiredAt = expiredAt,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return new ReviewerHistory(reviewer.Id, ratingIds);
    }

    private async Task<ActiveAssignments> CreateActiveAssignmentsAsync(int count)
    {
        await using var db = _fixture.NewOwnerDbContext();
        var reviewer = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var assignmentIds = new List<long>();

        for (var index = 0; index < count; index++)
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
            var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
            var request = await ReviewTestData.CreateQueuedRequestAsync(db, owner.Id, game.Id);
            request.Status = ReviewRequestStatus.Assigned;
            var assignment = new ReviewAssignment
            {
                RequestId = request.Id,
                GameId = game.Id,
                ReviewerId = reviewer.Id,
                ClaimOperationId = Guid.NewGuid(),
                Status = ReviewAssignmentStatus.Active,
                LeasedUntil = DateTimeOffset.UtcNow.AddHours(1),
            };
            db.ReviewAssignments.Add(assignment);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            assignmentIds.Add(assignment.Id);
        }

        return new ActiveAssignments(reviewer.Id, assignmentIds);
    }

    private async Task<ReviewerReputation> ReadStoredAsync(long reviewerId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.ReviewerReputations
            .AsNoTracking()
            .SingleAsync(
                item => item.ReviewerId == reviewerId,
                TestContext.Current.CancellationToken);
    }

    private static ReviewerReputationService NewService(IndieFi.Web.Data.AppDbContext db) =>
        new(db, NullLogger<ReviewerReputationService>.Instance);

    private sealed record ReviewerHistory(
        long ReviewerId,
        IReadOnlyList<long> RatingIds);

    private sealed record ActiveAssignments(
        long ReviewerId,
        IReadOnlyList<long> AssignmentIds);
}
