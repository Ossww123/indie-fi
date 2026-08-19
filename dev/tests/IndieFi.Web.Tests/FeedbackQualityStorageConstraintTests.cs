using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FeedbackQualityStorageConstraintTests
{
    private readonly PostgreSqlFixture _fixture;

    public FeedbackQualityStorageConstraintTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FeedbackRating_WhenValid_PersistsAndCanChangeOnlyMutableValues()
    {
        var feedback = await CreateSubmittedFeedbackAsync();

        long ratingId;
        await using (var insertDb = _fixture.NewDbContext())
        {
            var rating = new FeedbackRating
            {
                FeedbackSubmissionId = feedback.FeedbackId,
                RaterId = feedback.RaterId,
                ReviewerId = feedback.ReviewerId,
                Score = 5,
                Reason = "구체적인 개선점을 알려줘서 유용했습니다.",
            };
            insertDb.FeedbackRatings.Add(rating);
            await insertDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            ratingId = rating.Id;
        }

        await using (var updateDb = _fixture.NewDbContext())
        {
            var updated = await updateDb.Database.ExecuteSqlRawAsync(
                """
                UPDATE feedback_ratings
                   SET score = 4,
                       reason = NULL,
                       revision = revision + 1,
                       updated_at = now()
                 WHERE id = @ratingId
                """,
                [new NpgsqlParameter("ratingId", ratingId)],
                TestContext.Current.CancellationToken);
            Assert.Equal(1, updated);

            var identityMutation = await Assert.ThrowsAsync<PostgresException>(() =>
                updateDb.Database.ExecuteSqlRawAsync(
                    "UPDATE feedback_ratings SET reviewer_id = rater_id WHERE id = @ratingId",
                    [new NpgsqlParameter("ratingId", ratingId)],
                    TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, identityMutation.SqlState);

            var deletion = await Assert.ThrowsAsync<PostgresException>(() =>
                updateDb.Database.ExecuteSqlRawAsync(
                    "DELETE FROM feedback_ratings WHERE id = @ratingId",
                    [new NpgsqlParameter("ratingId", ratingId)],
                    TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deletion.SqlState);
        }

        await using var verificationDb = _fixture.NewOwnerDbContext();
        var stored = await verificationDb.Database.SqlQueryRaw<StoredRating>(
                """
                SELECT feedback_submission_id AS "FeedbackId",
                       rater_id AS "RaterId",
                       reviewer_id AS "ReviewerId",
                       score AS "Score",
                       reason AS "Reason",
                       revision AS "Revision",
                       created_at AS "CreatedAt",
                       updated_at AS "UpdatedAt"
                  FROM feedback_ratings
                 WHERE id = @ratingId
                """,
                new NpgsqlParameter("ratingId", ratingId))
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(feedback.FeedbackId, stored.FeedbackId);
        Assert.Equal(feedback.RaterId, stored.RaterId);
        Assert.Equal(feedback.ReviewerId, stored.ReviewerId);
        Assert.Equal(4, stored.Score);
        Assert.Null(stored.Reason);
        Assert.Equal(2, stored.Revision);
        Assert.True(stored.UpdatedAt >= stored.CreatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task FeedbackRating_WhenScoreIsOutsideFivePointScale_IsRejected(int score)
    {
        var feedback = await CreateSubmittedFeedbackAsync();

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_ratings (
                    feedback_submission_id, rater_id, reviewer_id, score)
                VALUES (@feedbackId, @raterId, @reviewerId, @score)
                """,
                [
                    new NpgsqlParameter("feedbackId", feedback.FeedbackId),
                    new NpgsqlParameter("raterId", feedback.RaterId),
                    new NpgsqlParameter("reviewerId", feedback.ReviewerId),
                    new NpgsqlParameter("score", score),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_feedback_ratings_score", exception.ConstraintName);
    }

    [Fact]
    public async Task FeedbackRating_WhenReasonIsNotNormalized_IsRejected()
    {
        var feedback = await CreateSubmittedFeedbackAsync();

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_ratings (
                    feedback_submission_id, rater_id, reviewer_id, score, reason)
                VALUES (@feedbackId, @raterId, @reviewerId, 3, '  ')
                """,
                [
                    new NpgsqlParameter("feedbackId", feedback.FeedbackId),
                    new NpgsqlParameter("raterId", feedback.RaterId),
                    new NpgsqlParameter("reviewerId", feedback.ReviewerId),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_feedback_ratings_reason", exception.ConstraintName);
    }

    [Fact]
    public async Task FeedbackRating_WhenTheSameFeedbackIsRatedTwice_IsRejected()
    {
        var feedback = await CreateSubmittedFeedbackAsync();

        await InsertRatingAsync(feedback, score: 4);

        await using var duplicateDb = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            duplicateDb.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_ratings (
                    feedback_submission_id, rater_id, reviewer_id, score)
                VALUES (@feedbackId, @raterId, @reviewerId, 5)
                """,
                [
                    new NpgsqlParameter("feedbackId", feedback.FeedbackId),
                    new NpgsqlParameter("raterId", feedback.RaterId),
                    new NpgsqlParameter("reviewerId", feedback.ReviewerId),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(
            "IX_feedback_ratings_feedback_submission_id",
            exception.ConstraintName);
    }

    [Fact]
    public async Task FeedbackRating_WhenReviewerDoesNotMatchTheSubmission_IsRejected()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        long otherReviewerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherReviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_ratings (
                    feedback_submission_id, rater_id, reviewer_id, score)
                VALUES (@feedbackId, @raterId, @reviewerId, 4)
                """,
                [
                    new NpgsqlParameter("feedbackId", feedback.FeedbackId),
                    new NpgsqlParameter("raterId", feedback.RaterId),
                    new NpgsqlParameter("reviewerId", otherReviewerId),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal(
            "FK_feedback_ratings_feedback_submission_reviewer",
            exception.ConstraintName);
    }

    [Fact]
    public async Task FeedbackRating_WhenRaterAndReviewerAreTheSame_IsRejected()
    {
        var feedback = await CreateSubmittedFeedbackAsync();

        await using var db = _fixture.NewOwnerDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_ratings (
                    feedback_submission_id, rater_id, reviewer_id, score)
                VALUES (@feedbackId, @reviewerId, @reviewerId, 4)
                """,
                [
                    new NpgsqlParameter("feedbackId", feedback.FeedbackId),
                    new NpgsqlParameter("reviewerId", feedback.ReviewerId),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_feedback_ratings_distinct_users", exception.ConstraintName);
    }

    [Fact]
    public async Task FeedbackRating_WhenRaterDoesNotOwnTheReviewedGame_IsRejected()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        long unrelatedUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            unrelatedUserId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_ratings (
                    feedback_submission_id, rater_id, reviewer_id, score)
                VALUES (@feedbackId, @raterId, @reviewerId, 4)
                """,
                [
                    new NpgsqlParameter("feedbackId", feedback.FeedbackId),
                    new NpgsqlParameter("raterId", unrelatedUserId),
                    new NpgsqlParameter("reviewerId", feedback.ReviewerId),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_feedback_ratings_rater_owns_feedback", exception.ConstraintName);
    }

    [Fact]
    public async Task ReviewerReputation_WhenAggregatesAreConsistent_PersistsForOneReviewer()
    {
        long reviewerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        await using (var db = _fixture.NewDbContext())
        {
            db.ReviewerReputations.Add(new ReviewerReputation
            {
                ReviewerId = reviewerId,
                CompletedReviewCount = 5,
                ExpiredAssignmentCount = 1,
                RatingCount = 3,
                RatingScoreSum = 12,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var duplicateDb = _fixture.NewDbContext();
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
            duplicateDb.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO reviewer_reputations (
                    reviewer_id,
                    completed_review_count,
                    expired_assignment_count,
                    rating_count,
                    rating_score_sum)
                VALUES (@reviewerId, 0, 0, 0, 0)
                """,
                [new NpgsqlParameter("reviewerId", reviewerId)],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("PK_reviewer_reputations", duplicate.ConstraintName);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, "CK_reviewer_reputations_counts")]
    [InlineData(0, -1, 0, 0, "CK_reviewer_reputations_counts")]
    [InlineData(0, 0, 0, 1, "CK_reviewer_reputations_rating_aggregate")]
    [InlineData(0, 0, 2, 11, "CK_reviewer_reputations_rating_aggregate")]
    public async Task ReviewerReputation_WhenAggregatesAreInvalid_IsRejected(
        int completedCount,
        int expiredCount,
        int ratingCount,
        int ratingScoreSum,
        string constraintName)
    {
        long reviewerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO reviewer_reputations (
                    reviewer_id,
                    completed_review_count,
                    expired_assignment_count,
                    rating_count,
                    rating_score_sum)
                VALUES (@reviewerId, @completedCount, @expiredCount, @ratingCount, @ratingScoreSum)
                """,
                [
                    new NpgsqlParameter("reviewerId", reviewerId),
                    new NpgsqlParameter("completedCount", completedCount),
                    new NpgsqlParameter("expiredCount", expiredCount),
                    new NpgsqlParameter("ratingCount", ratingCount),
                    new NpgsqlParameter("ratingScoreSum", ratingScoreSum),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    private async Task<SubmittedFeedback> CreateSubmittedFeedbackAsync()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using (var submissionDb = _fixture.NewDbContext())
        {
            var result = await new FeedbackSubmissionService(
                    submissionDb,
                    NullLogger<FeedbackSubmissionService>.Instance,
                    new TestNotificationEventPublisher())
                .SubmitAsync(
                    assignment.AssignmentId,
                    assignment.ReviewerId,
                    "The pacing feedback includes concrete and useful improvement steps.");
            Assert.Equal(FeedbackSubmissionOutcome.Submitted, result.Outcome);
        }

        await using var queryDb = _fixture.NewDbContext();
        return await (
                from submission in queryDb.FeedbackSubmissions.AsNoTracking()
                join reviewAssignment in queryDb.ReviewAssignments.AsNoTracking()
                    on submission.AssignmentId equals reviewAssignment.Id
                join reviewRequest in queryDb.ReviewRequests.AsNoTracking()
                    on reviewAssignment.RequestId equals reviewRequest.Id
                where submission.AssignmentId == assignment.AssignmentId
                select new SubmittedFeedback(
                    submission.Id,
                    reviewRequest.RequesterId,
                    submission.ReviewerId))
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task InsertRatingAsync(SubmittedFeedback feedback, int score)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO feedback_ratings (
                feedback_submission_id, rater_id, reviewer_id, score)
            VALUES (@feedbackId, @raterId, @reviewerId, @score)
            """,
            [
                new NpgsqlParameter("feedbackId", feedback.FeedbackId),
                new NpgsqlParameter("raterId", feedback.RaterId),
                new NpgsqlParameter("reviewerId", feedback.ReviewerId),
                new NpgsqlParameter("score", score),
            ],
            TestContext.Current.CancellationToken);
    }

    private sealed record SubmittedFeedback(
        long FeedbackId,
        long RaterId,
        long ReviewerId);

    private sealed record StoredRating(
        long FeedbackId,
        long RaterId,
        long ReviewerId,
        int Score,
        string? Reason,
        int Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
