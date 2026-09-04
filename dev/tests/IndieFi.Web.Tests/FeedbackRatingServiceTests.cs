using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FeedbackRatingServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public FeedbackRatingServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SaveAsync_WhenInputIsValid_CreatesNormalizedRatingWithoutChangingCredits()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        var before = await ReadCreditStateAsync(feedback.RaterId);

        FeedbackRatingSaveResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 5,
                reasonInput: "  구체적인 개선 방향을 제시했습니다.  ",
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.FeedbackRatings
            .AsNoTracking()
            .SingleAsync(
                rating => rating.FeedbackSubmissionId == feedback.FeedbackId,
                TestContext.Current.CancellationToken);
        var after = await ReadCreditStateAsync(feedback.RaterId);

        Assert.Equal(FeedbackRatingSaveOutcome.Created, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(stored.Id, result.RatingId);
        Assert.Equal(1, result.Revision);
        Assert.Equal(feedback.RaterId, stored.RaterId);
        Assert.Equal(feedback.ReviewerId, stored.ReviewerId);
        Assert.Equal(5, stored.Score);
        Assert.Equal("구체적인 개선 방향을 제시했습니다.", stored.Reason);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SaveAsync_WhenInputIsInvalid_ReturnsViolationsAndStoresNothing()
    {
        var feedback = await CreateSubmittedFeedbackAsync();

        FeedbackRatingSaveResult invalidScore;
        FeedbackRatingSaveResult longReason;
        FeedbackRatingSaveResult invalidRevision;
        await using (var db = _fixture.NewDbContext())
        {
            var service = NewService(db);
            invalidScore = await service.SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 0,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
            longReason = await service.SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 3,
                reasonInput: new string('가', FeedbackQualityPolicy.MaximumReasonLength + 1),
                expectedRevision: null,
                TestContext.Current.CancellationToken);
            invalidRevision = await service.SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 3,
                reasonInput: null,
                expectedRevision: 0,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(FeedbackRatingSaveOutcome.Invalid, invalidScore.Outcome);
        Assert.Contains(invalidScore.Violations, item => item.Field == "Score");
        Assert.Equal(FeedbackRatingSaveOutcome.Invalid, longReason.Outcome);
        Assert.Contains(longReason.Violations, item => item.Field == "Reason");
        Assert.Equal(FeedbackRatingSaveOutcome.Invalid, invalidRevision.Outcome);
        Assert.Contains(invalidRevision.Violations, item => item.Field == "Revision");

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.FeedbackRatings.AnyAsync(
            rating => rating.FeedbackSubmissionId == feedback.FeedbackId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_WhenReasonContainsOnlyWhitespace_StoresNoReason()
    {
        var feedback = await CreateSubmittedFeedbackAsync();

        await using (var db = _fixture.NewDbContext())
        {
            var result = await NewService(db).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 3,
                reasonInput: " \t\r\n ",
                expectedRevision: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(FeedbackRatingSaveOutcome.Created, result.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Null(await verificationDb.FeedbackRatings
            .Where(rating => rating.FeedbackSubmissionId == feedback.FeedbackId)
            .Select(rating => rating.Reason)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_WhenFeedbackIsMissingOrOwnedBySomeoneElse_ReturnsNotFound()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        long otherUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherUserId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        FeedbackRatingSaveResult unauthorized;
        FeedbackRatingSaveResult missing;
        await using (var db = _fixture.NewDbContext())
        {
            var service = NewService(db);
            unauthorized = await service.SaveAsync(
                feedback.FeedbackId,
                otherUserId,
                score: 4,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
            missing = await service.SaveAsync(
                long.MaxValue,
                feedback.RaterId,
                score: 4,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(FeedbackRatingSaveOutcome.NotFound, unauthorized.Outcome);
        Assert.Equal(FeedbackRatingSaveOutcome.NotFound, missing.Outcome);
    }

    [Fact]
    public async Task SaveAsync_WhenAccountIsSuspended_ReturnsAccountUnavailable()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        await using (var suspensionDb = _fixture.NewOwnerDbContext())
        {
            var owner = await suspensionDb.Users.SingleAsync(
                user => user.Id == feedback.RaterId,
                TestContext.Current.CancellationToken);
            owner.AccountStatus = AccountStatus.IndefinitelySuspended;
            owner.SuspendedUntil = null;
            await suspensionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db).SaveAsync(
            feedback.FeedbackId,
            feedback.RaterId,
            score: 4,
            reasonInput: null,
            expectedRevision: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackRatingSaveOutcome.AccountUnavailable, result.Outcome);
    }

    [Fact]
    public async Task SaveAsync_WhenThirtyDayWindowHasClosed_RejectsCreateAndUpdate()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        FeedbackRatingSaveResult created;
        await using (var createDb = _fixture.NewDbContext())
        {
            created = await NewService(createDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 4,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        await using (var expirationDb = _fixture.NewOwnerDbContext())
        {
            await expirationDb.FeedbackSubmissions
                .Where(submission => submission.Id == feedback.FeedbackId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        submission => submission.CreatedAt,
                        DateTimeOffset.UtcNow.AddDays(-31)),
                    TestContext.Current.CancellationToken);
        }

        FeedbackRatingSaveResult update;
        await using (var updateDb = _fixture.NewDbContext())
        {
            update = await NewService(updateDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 5,
                reasonInput: null,
                expectedRevision: created.Revision,
                TestContext.Current.CancellationToken);
        }

        var unratedFeedback = await CreateSubmittedFeedbackAsync();
        await using (var expirationDb = _fixture.NewOwnerDbContext())
        {
            await expirationDb.FeedbackSubmissions
                .Where(submission => submission.Id == unratedFeedback.FeedbackId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        submission => submission.CreatedAt,
                        DateTimeOffset.UtcNow.AddDays(-31)),
                    TestContext.Current.CancellationToken);
        }

        FeedbackRatingSaveResult createAfterDeadline;
        await using (var createDb = _fixture.NewDbContext())
        {
            createAfterDeadline = await NewService(createDb).SaveAsync(
                unratedFeedback.FeedbackId,
                unratedFeedback.RaterId,
                score: 4,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(FeedbackRatingSaveOutcome.WindowClosed, update.Outcome);
        Assert.Equal(FeedbackRatingSaveOutcome.WindowClosed, createAfterDeadline.Outcome);
    }

    [Fact]
    public async Task SaveAsync_WhenRevisionMatches_UpdatesAndMakesRetryIdempotent()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        FeedbackRatingSaveResult created;
        await using (var createDb = _fixture.NewDbContext())
        {
            created = await NewService(createDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 3,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        FeedbackRatingSaveResult updated;
        FeedbackRatingSaveResult retried;
        await using (var updateDb = _fixture.NewDbContext())
        {
            var service = NewService(updateDb);
            updated = await service.SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 5,
                reasonInput: "  반영된 평가  ",
                expectedRevision: created.Revision,
                TestContext.Current.CancellationToken);
            retried = await service.SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 5,
                reasonInput: "반영된 평가",
                expectedRevision: created.Revision,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(FeedbackRatingSaveOutcome.Updated, updated.Outcome);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(FeedbackRatingSaveOutcome.Unchanged, retried.Outcome);
        Assert.Equal(2, retried.Revision);
    }

    [Fact]
    public async Task SaveAsync_WhenRevisionIsStaleAndValuesDiffer_ReturnsConflict()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        FeedbackRatingSaveResult created;
        await using (var createDb = _fixture.NewDbContext())
        {
            created = await NewService(createDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 3,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        await using (var firstUpdateDb = _fixture.NewDbContext())
        {
            var firstUpdate = await NewService(firstUpdateDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 4,
                reasonInput: null,
                expectedRevision: created.Revision,
                TestContext.Current.CancellationToken);
            Assert.Equal(FeedbackRatingSaveOutcome.Updated, firstUpdate.Outcome);
        }

        FeedbackRatingSaveResult conflict;
        await using (var staleDb = _fixture.NewDbContext())
        {
            conflict = await NewService(staleDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 5,
                reasonInput: null,
                expectedRevision: created.Revision,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(FeedbackRatingSaveOutcome.Conflict, conflict.Outcome);
        Assert.Equal(2, conflict.Revision);
    }

    [Fact]
    public async Task SaveAsync_WhenDifferentCreateFollowsExistingRating_ReturnsConflict()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        await using (var createDb = _fixture.NewDbContext())
        {
            var created = await NewService(createDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 3,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
            Assert.Equal(FeedbackRatingSaveOutcome.Created, created.Outcome);
        }

        FeedbackRatingSaveResult conflict;
        await using (var duplicateDb = _fixture.NewDbContext())
        {
            conflict = await NewService(duplicateDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 5,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(FeedbackRatingSaveOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, conflict.Revision);
    }

    [Fact]
    public async Task SaveAsync_WhenSameCreateRunsConcurrently_CreatesOneRating()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<FeedbackRatingSaveResult> SaveAsync() => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = _fixture.NewDbContext();
            return await NewService(db).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 4,
                reasonInput: "동시에 전송된 평가",
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        });

        var first = SaveAsync();
        var second = SaveAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Created));
        Assert.Equal(1, results.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Unchanged));

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.FeedbackRatings.CountAsync(
            rating => rating.FeedbackSubmissionId == feedback.FeedbackId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_WhenDifferentUpdatesUseSameRevision_UpdatesOnlyOne()
    {
        var feedback = await CreateSubmittedFeedbackAsync();
        FeedbackRatingSaveResult created;
        await using (var createDb = _fixture.NewDbContext())
        {
            created = await NewService(createDb).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score: 3,
                reasonInput: null,
                expectedRevision: null,
                TestContext.Current.CancellationToken);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FeedbackRatingSaveResult> UpdateAsync(int score) => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = _fixture.NewDbContext();
            return await NewService(db).SaveAsync(
                feedback.FeedbackId,
                feedback.RaterId,
                score,
                reasonInput: null,
                expectedRevision: created.Revision,
                TestContext.Current.CancellationToken);
        });

        var first = UpdateAsync(4);
        var second = UpdateAsync(5);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Updated));
        Assert.Equal(1, results.Count(result => result.Outcome == FeedbackRatingSaveOutcome.Conflict));

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(2, await verificationDb.FeedbackRatings
            .Where(rating => rating.FeedbackSubmissionId == feedback.FeedbackId)
            .Select(rating => rating.Revision)
            .SingleAsync(TestContext.Current.CancellationToken));
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
                    "The feedback contains enough concrete detail to support a useful rating.");
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
                TestContext.Current.CancellationToken));
    }

    private static FeedbackRatingService NewService(IndieFi.Web.Data.AppDbContext db) =>
        new(db, NullLogger<FeedbackRatingService>.Instance);

    private sealed record SubmittedFeedback(
        long FeedbackId,
        long RaterId,
        long ReviewerId);

    private sealed record CreditState(int Balance, int LedgerEntryCount);
}
