using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FeedbackSubmissionServiceTests
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    private readonly PostgreSqlFixture _fixture;

    public FeedbackSubmissionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SubmitAsync_WhenTheReviewerOwnsALiveAssignment_CompletesTheAssignmentAndTheRequest()
    {
        const string Body = "The controls feel responsive and predictable.";
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        FeedbackSubmissionResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewSubmissionService(serviceDb).SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                Body);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var assignmentState = await verificationDb.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => new { item.Status, item.CompletedAt, item.ExpiredAt })
            .SingleAsync(TestContext.Current.CancellationToken);
        var requestState = await verificationDb.ReviewRequests
            .Where(item => item.Id == assignment.RequestId)
            .Select(item => new { item.Status, item.CompletedAt, item.CancelledAt })
            .SingleAsync(TestContext.Current.CancellationToken);
        var submissions = await verificationDb.FeedbackSubmissions
            .Where(item => item.AssignmentId == assignment.AssignmentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.Submitted, result.Outcome);
        Assert.Equal(ReviewAssignmentStatus.Completed, assignmentState.Status);
        Assert.NotNull(assignmentState.CompletedAt);
        Assert.Null(assignmentState.ExpiredAt);
        Assert.Equal(ReviewRequestStatus.Completed, requestState.Status);
        Assert.NotNull(requestState.CompletedAt);
        Assert.Null(requestState.CancelledAt);
        var submission = Assert.Single(submissions);
        Assert.Equal(Body, submission.Body);
    }

    [Fact]
    public async Task SubmitAsync_WhenTheSubmissionSucceeds_EarnsExactlyOneCredit()
    {
        const string Body = "The difficulty curve feels fair and rewarding.";
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        int balanceBefore;

        await using (var beforeDb = _fixture.NewDbContext())
        {
            balanceBefore = await beforeDb.Users
                .Where(user => user.Id == assignment.ReviewerId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        FeedbackSubmissionResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewSubmissionService(serviceDb).SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                Body);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var earnEntries = await verificationDb.CreditLedgerEntries
            .Where(entry => entry.UserId == assignment.ReviewerId)
            .Where(entry => entry.Kind == CreditEntryKind.Earn)
            .ToListAsync(TestContext.Current.CancellationToken);
        var balanceAfter = await verificationDb.Users
            .Where(user => user.Id == assignment.ReviewerId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.Submitted, result.Outcome);
        var earnEntry = Assert.Single(earnEntries);
        Assert.Equal(1, earnEntry.Delta);
        Assert.Equal(balanceBefore + 1, balanceAfter);
    }

    [Fact]
    public async Task SubmitAsync_WhenTheAssignmentBelongsToSomeoneElse_ReportsNotSubmittable()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        long otherReviewerId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherReviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        FeedbackSubmissionResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewSubmissionService(serviceDb).SubmitAsync(
                assignment.AssignmentId,
                otherReviewerId,
                "This reviewer does not own the assignment.");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var assignmentStatus = await verificationDb.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var requestStatus = await verificationDb.ReviewRequests
            .Where(item => item.Id == assignment.RequestId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var submissionCount = await verificationDb.FeedbackSubmissions.CountAsync(
            item => item.AssignmentId == assignment.AssignmentId,
            TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.NotSubmittable, result.Outcome);
        Assert.Equal(ReviewAssignmentStatus.Active, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Assigned, requestStatus);
        Assert.Equal(0, submissionCount);
    }

    [Fact]
    public async Task SubmitAsync_WhenTheSubmissionIsRefused_EarnsNothing()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        long otherReviewerId;
        int balanceBefore;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherReviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            balanceBefore = await arrangeDb.Users
                .Where(user => user.Id == otherReviewerId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        FeedbackSubmissionResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewSubmissionService(serviceDb).SubmitAsync(
                assignment.AssignmentId,
                otherReviewerId,
                "This reviewer cannot submit someone else's assignment.");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var earnEntryCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == otherReviewerId && entry.Kind == CreditEntryKind.Earn,
            TestContext.Current.CancellationToken);
        var balanceAfter = await verificationDb.Users
            .Where(user => user.Id == otherReviewerId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var submissionCount = await verificationDb.FeedbackSubmissions.CountAsync(
            submission => submission.AssignmentId == assignment.AssignmentId,
            TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.NotSubmittable, result.Outcome);
        Assert.Equal(0, earnEntryCount);
        Assert.Equal(balanceBefore, balanceAfter);
        Assert.Equal(0, submissionCount);
    }

    [Fact]
    public async Task SubmitAsync_WhenTheLeaseHasExpired_ReportsLeaseExpired()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        await using (var expirationDb = _fixture.NewDbContext())
        {
            var storedAssignment = await expirationDb.ReviewAssignments.SingleAsync(
                item => item.Id == assignment.AssignmentId,
                TestContext.Current.CancellationToken);
            storedAssignment.LeasedUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            await expirationDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        FeedbackSubmissionResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewSubmissionService(serviceDb).SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                "This feedback arrived after the lease expired.");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var assignmentStatus = await verificationDb.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var submissionCount = await verificationDb.FeedbackSubmissions.CountAsync(
            item => item.AssignmentId == assignment.AssignmentId,
            TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.LeaseExpired, result.Outcome);
        Assert.Equal(ReviewAssignmentStatus.Active, assignmentStatus);
        Assert.Equal(0, submissionCount);
    }

    [Fact]
    public async Task SubmitAsync_WhenCalledTwice_TheSecondIsRefusedAndTheFirstFeedbackStays()
    {
        const string FirstBody = "Keep the first submission exactly as it was.";
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        FeedbackSubmissionResult firstResult;
        FeedbackSubmissionResult secondResult;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            var service = NewSubmissionService(serviceDb);
            firstResult = await service.SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                FirstBody);
            secondResult = await service.SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                "The second submission must not replace the first.");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var submissions = await verificationDb.FeedbackSubmissions
            .Where(item => item.AssignmentId == assignment.AssignmentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.Submitted, firstResult.Outcome);
        Assert.Equal(FeedbackSubmissionOutcome.NotSubmittable, secondResult.Outcome);
        var submission = Assert.Single(submissions);
        Assert.Equal(FirstBody, submission.Body);
    }

    [Fact]
    public async Task SubmittingTwiceForOneAssignment_IsRefusedByTheDatabase()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        await using (var serviceDb = _fixture.NewDbContext())
        {
            var result = await NewSubmissionService(serviceDb).SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                "The service stores the first submission.");
            Assert.Equal(FeedbackSubmissionOutcome.Submitted, result.Outcome);
        }

        await using var attemptDb = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await attemptDb.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_submissions (assignment_id, body)
                VALUES ({0}, {1})
                """,
                new object[]
                {
                    assignment.AssignmentId,
                    "The database must reject this duplicate.",
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task SubmittingFeedbackShorterThanTheMinimum_IsRefusedByTheDatabase()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using var attemptDb = _fixture.NewDbContext();

        var shortBodyException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await attemptDb.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_submissions (assignment_id, body)
                VALUES ({0}, {1})
                """,
                new object[]
                {
                    assignment.AssignmentId,
                    "짧다",
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(CheckViolation, shortBodyException.SqlState);

        var whitespaceBodyException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await attemptDb.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_submissions (assignment_id, body)
                VALUES ({0}, {1})
                """,
                new object[]
                {
                    assignment.AssignmentId,
                    new string(' ', 30),
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(CheckViolation, whitespaceBodyException.SqlState);
    }

    private static FeedbackSubmissionService NewSubmissionService(AppDbContext db) =>
        new(db, NullLogger<FeedbackSubmissionService>.Instance);
}
