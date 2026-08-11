using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FeedbackSubmissionServiceTests
{
    private const string UniqueViolation = "23505";

    private readonly PostgreSqlFixture _fixture;

    public FeedbackSubmissionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SubmitAsync_WhenTheReviewerOwnsALiveAssignment_CompletesTheAssignmentAndTheRequest()
    {
        const string Body = "The controls feel responsive and predictable.";
        var assignment = await CreateLiveAssignmentAsync();

        FeedbackSubmissionResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewSubmissionService(serviceDb).SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                Body);
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
        var submissions = await verificationDb.FeedbackSubmissions
            .Where(item => item.AssignmentId == assignment.AssignmentId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.Submitted, result.Outcome);
        Assert.Equal(ReviewAssignmentStatus.Completed, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Completed, requestStatus);
        var submission = Assert.Single(submissions);
        Assert.Equal(Body, submission.Body);
    }

    [Fact]
    public async Task SubmitAsync_WhenTheAssignmentBelongsToSomeoneElse_ReportsNotSubmittable()
    {
        var assignment = await CreateLiveAssignmentAsync();
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
    public async Task SubmitAsync_WhenTheLeaseHasExpired_ReportsLeaseExpired()
    {
        var assignment = await CreateLiveAssignmentAsync();

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
        const string FirstBody = "Keep the first submission.";
        var assignment = await CreateLiveAssignmentAsync();

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
        var assignment = await CreateLiveAssignmentAsync();

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

    private async Task<LiveAssignment> CreateLiveAssignmentAsync()
    {
        long reviewerId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 1);
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
        }

        await using var claimDb = _fixture.NewDbContext();
        var claimResult = await new ReviewClaimService(
            claimDb,
            NullLogger<ReviewClaimService>.Instance).ClaimNextAsync(reviewerId);
        var assignmentId = await claimDb.ReviewAssignments
            .Where(item => item.RequestId == requestId)
            .Select(item => item.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.Claimed, claimResult.Outcome);
        Assert.Equal(requestId, claimResult.RequestId);
        return new LiveAssignment(assignmentId, requestId, reviewerId);
    }

    private static FeedbackSubmissionService NewSubmissionService(AppDbContext db) =>
        new(db, NullLogger<FeedbackSubmissionService>.Instance);

    private sealed record LiveAssignment(long AssignmentId, long RequestId, long ReviewerId);
}
