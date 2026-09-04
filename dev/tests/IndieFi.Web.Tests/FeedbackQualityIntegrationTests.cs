using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FeedbackQualityIntegrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public FeedbackQualityIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentDuplicateRatingAndUnauthorizedAttempt_StoreOneOwnedRating()
    {
        var history = await CreateCompletedHistoryAsync([null]);
        var feedback = history.Feedback.Single();
        long otherUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherUserId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FeedbackRatingSaveResult> SaveAsync(long raterId) => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = _fixture.NewDbContext();
            return await NewRatingService(db).SaveAsync(
                feedback.FeedbackId,
                raterId,
                score: 4,
                reasonInput: "동시에 등록한 평가",
                expectedRevision: null,
                cancellationToken);
        }, cancellationToken);

        var firstOwnerSave = SaveAsync(feedback.OwnerId);
        var secondOwnerSave = SaveAsync(feedback.OwnerId);
        var unauthorizedSave = SaveAsync(otherUserId);
        gate.SetResult();

        var ownerResults = await Task.WhenAll(firstOwnerSave, secondOwnerSave);
        var unauthorizedResult = await unauthorizedSave;

        Assert.Equal(
            1,
            ownerResults.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Created));
        Assert.Equal(
            1,
            ownerResults.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Unchanged));
        Assert.Equal(FeedbackRatingSaveOutcome.NotFound, unauthorizedResult.Outcome);

        await using var verificationDb = _fixture.NewDbContext();
        var rating = await verificationDb.FeedbackRatings
            .AsNoTracking()
            .SingleAsync(
                item => item.FeedbackSubmissionId == feedback.FeedbackId,
                cancellationToken);
        var reputation = await verificationDb.ReviewerReputations
            .AsNoTracking()
            .SingleAsync(
                item => item.ReviewerId == history.ReviewerId,
                cancellationToken);

        Assert.Equal(feedback.OwnerId, rating.RaterId);
        Assert.Equal(history.ReviewerId, rating.ReviewerId);
        Assert.Equal(1, reputation.CompletedReviewCount);
        Assert.Equal(1, reputation.RatingCount);
        Assert.Equal(4, reputation.RatingScoreSum);
    }

    [Fact]
    public async Task ConcurrentRatingUpdates_KeepProjectionRecalculationAndCreditsConsistent()
    {
        var history = await CreateCompletedHistoryAsync([1, 1, 3, null, null]);
        var target = history.Feedback[2];
        var reviewerCreditBefore = await ReadCreditStateAsync(history.ReviewerId);
        var ownerCreditBefore = await ReadCreditStateAsync(target.OwnerId);
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<FeedbackRatingSaveResult> UpdateAsync(int score) => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = _fixture.NewDbContext();
            return await NewRatingService(db).SaveAsync(
                target.FeedbackId,
                target.OwnerId,
                score,
                reasonInput: $"동시 수정 점수 {score}",
                expectedRevision: 1,
                cancellationToken);
        }, cancellationToken);

        var firstUpdate = UpdateAsync(1);
        var secondUpdate = UpdateAsync(5);
        gate.SetResult();
        var results = await Task.WhenAll(firstUpdate, secondUpdate);

        Assert.Equal(
            1,
            results.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Updated));
        Assert.Equal(
            1,
            results.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Conflict));

        int finalScore;
        await using (var resultDb = _fixture.NewDbContext())
        {
            var stored = await resultDb.FeedbackRatings
                .AsNoTracking()
                .SingleAsync(
                    item => item.FeedbackSubmissionId == target.FeedbackId,
                    cancellationToken);
            Assert.Equal(2, stored.Revision);
            finalScore = stored.Score;
        }

        ReviewerReputationSnapshot beforeRecalculation;
        ReviewerReputationSnapshot afterRecalculation;
        await using (var reputationDb = _fixture.NewDbContext())
        {
            var service = NewReputationService(reputationDb);
            beforeRecalculation = Assert.IsType<ReviewerReputationSnapshot>(
                await service.GetAsync(history.ReviewerId, cancellationToken));
            afterRecalculation = Assert.IsType<ReviewerReputationSnapshot>(
                await service.RecalculateAsync(history.ReviewerId, cancellationToken));
        }

        var expectedScores = ReviewerReputationPolicy.Calculate(
            completedReviewCount: 5,
            expiredAssignmentCount: 0,
            ratingCount: 3,
            ratingScoreSum: 2 + finalScore);
        Assert.Equal(5, beforeRecalculation.CompletedReviewCount);
        Assert.Equal(3, beforeRecalculation.RatingCount);
        Assert.Equal(2 + finalScore, beforeRecalculation.RatingScoreSum);
        Assert.Equal(expectedScores.Grade, beforeRecalculation.Grade);
        Assert.Equal(beforeRecalculation, afterRecalculation);

        Assert.Equal(
            reviewerCreditBefore,
            await ReadCreditStateAsync(history.ReviewerId));
        Assert.Equal(ownerCreditBefore, await ReadCreditStateAsync(target.OwnerId));
    }

    [Fact]
    public async Task ReviewerGradeChange_DoesNotChangeOldestRequestClaimOrder()
    {
        long reviewerId;
        long firstRequestId;
        long secondRequestId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);

            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var requester = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 2);
            var firstGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            var secondGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            firstRequestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                firstGame.Id)).Id;
            secondRequestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                secondGame.Id)).Id;
        }

        ReviewClaimResult firstClaim;
        await using (var firstClaimDb = _fixture.NewDbContext())
        {
            firstClaim = await NewClaimService(firstClaimDb).ClaimNextAsync(reviewerId);
        }

        await using (var gradeDb = _fixture.NewDbContext())
        {
            var reputation = await gradeDb.ReviewerReputations.SingleAsync(
                item => item.ReviewerId == reviewerId,
                TestContext.Current.CancellationToken);
            reputation.CompletedReviewCount = 20;
            reputation.RatingCount = 20;
            reputation.RatingScoreSum = 100;
            await gradeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            var grade = ReviewerReputationPolicy.Calculate(
                reputation.CompletedReviewCount,
                reputation.ExpiredAssignmentCount,
                reputation.RatingCount,
                reputation.RatingScoreSum).Grade;
            Assert.Equal(ReviewerGrade.Platinum, grade);
        }

        ReviewClaimResult secondClaim;
        await using (var secondClaimDb = _fixture.NewDbContext())
        {
            secondClaim = await NewClaimService(secondClaimDb).ClaimNextAsync(reviewerId);
        }

        Assert.Equal(ReviewClaimOutcome.Claimed, firstClaim.Outcome);
        Assert.Equal(firstRequestId, firstClaim.RequestId);
        Assert.Equal(ReviewClaimOutcome.Claimed, secondClaim.Outcome);
        Assert.Equal(secondRequestId, secondClaim.RequestId);
    }

    private async Task<ReviewerHistory> CreateCompletedHistoryAsync(
        IReadOnlyList<int?> ratingScores)
    {
        await using var db = _fixture.NewDbContext();
        var reviewer = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var feedback = new List<CompletedFeedback>();

        foreach (var ratingScore in ratingScores)
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
            var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
            var request = await ReviewTestData.CreateQueuedRequestAsync(
                db,
                owner.Id,
                game.Id);
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
                Body = $"통합 검증을 위한 충분히 구체적인 피드백 본문 {Guid.NewGuid():N}",
            };
            db.CreditLedgerEntries.Add(earnEntry);
            db.FeedbackSubmissions.Add(submission);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            if (ratingScore is int score)
            {
                db.FeedbackRatings.Add(new FeedbackRating
                {
                    FeedbackSubmissionId = submission.Id,
                    RaterId = owner.Id,
                    ReviewerId = reviewer.Id,
                    Score = score,
                });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            feedback.Add(new CompletedFeedback(submission.Id, owner.Id));
        }

        return new ReviewerHistory(reviewer.Id, feedback);
    }

    private async Task<CreditState> ReadCreditStateAsync(long userId)
    {
        await using var db = _fixture.NewDbContext();
        return new CreditState(
            await db.Users
                .Where(user => user.Id == userId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken),
            await db.CreditLedgerEntries.CountAsync(
                entry => entry.UserId == userId,
                TestContext.Current.CancellationToken),
            await db.CreditLedgerEntries
                .Where(entry => entry.UserId == userId)
                .SumAsync(entry => entry.Delta, TestContext.Current.CancellationToken));
    }

    private static FeedbackRatingService NewRatingService(AppDbContext db) =>
        new(db, NullLogger<FeedbackRatingService>.Instance);

    private static ReviewerReputationService NewReputationService(AppDbContext db) =>
        new(db, NullLogger<ReviewerReputationService>.Instance);

    private static ReviewClaimService NewClaimService(AppDbContext db) =>
        new(
            db,
            NullLogger<ReviewClaimService>.Instance,
            new TestNotificationEventPublisher());

    private sealed record ReviewerHistory(
        long ReviewerId,
        IReadOnlyList<CompletedFeedback> Feedback);

    private sealed record CompletedFeedback(long FeedbackId, long OwnerId);

    private sealed record CreditState(
        int Balance,
        int LedgerEntryCount,
        int LedgerDeltaSum);
}
