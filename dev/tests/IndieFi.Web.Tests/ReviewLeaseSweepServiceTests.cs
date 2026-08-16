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
public sealed class ReviewLeaseSweepServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReviewLeaseSweepServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SweepAsync_WhenALeaseHasExpired_ExpiresTheAssignmentAndRequeuesTheRequest()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        await using (var expirationDb = _fixture.NewDbContext())
        {
            await expirationDb.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_assignments
                   SET leased_until = now() - INTERVAL '1 minute'
                 WHERE id = {0}
                """,
                [assignment.AssignmentId],
                TestContext.Current.CancellationToken);
        }

        ReviewLeaseSweepResult result;
        await using (var sweepDb = _fixture.NewDbContext())
        {
            result = await NewSweepService(sweepDb).SweepAsync();
        }

        await using var verificationDb = _fixture.NewDbContext();
        var assignmentState = await verificationDb.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => new { item.Status, item.CompletedAt, item.ExpiredAt })
            .SingleAsync(TestContext.Current.CancellationToken);
        var requestStatus = await verificationDb.ReviewRequests
            .Where(item => item.Id == assignment.RequestId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExpiredCount);
        Assert.Equal(ReviewAssignmentStatus.Expired, assignmentState.Status);
        Assert.Null(assignmentState.CompletedAt);
        Assert.NotNull(assignmentState.ExpiredAt);
        Assert.Equal(ReviewRequestStatus.Queued, requestStatus);
    }

    [Fact]
    public async Task SweepAsync_WhenTheLeaseIsStillLive_LeavesTheAssignmentAlone()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        ReviewLeaseSweepResult result;
        await using (var sweepDb = _fixture.NewDbContext())
        {
            result = await NewSweepService(sweepDb).SweepAsync();
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

        Assert.Equal(0, result.ExpiredCount);
        Assert.Equal(ReviewAssignmentStatus.Active, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Assigned, requestStatus);
    }

    [Fact]
    public async Task SweepAsync_WhenACompletedAssignmentIsPastItsLease_LeavesItCompleted()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        await using (var submissionDb = _fixture.NewDbContext())
        {
            var submissionResult = await NewSubmissionService(submissionDb).SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                "The completed review must not be returned to the queue.");
            Assert.Equal(FeedbackSubmissionOutcome.Submitted, submissionResult.Outcome);
        }

        await using (var expirationDb = _fixture.NewDbContext())
        {
            await expirationDb.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_assignments
                   SET leased_until = now() - INTERVAL '1 minute'
                 WHERE id = {0}
                """,
                [assignment.AssignmentId],
                TestContext.Current.CancellationToken);
        }

        ReviewLeaseSweepResult result;
        await using (var sweepDb = _fixture.NewDbContext())
        {
            result = await NewSweepService(sweepDb).SweepAsync();
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

        Assert.Equal(0, result.ExpiredCount);
        Assert.Equal(ReviewAssignmentStatus.Completed, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Completed, requestStatus);
    }

    // Claiming, submitting and sweeping each move the assignment and its request together, and
    // IX_review_assignments_request_id allows a request only one assignment that is not expired,
    // so an active assignment always belongs to an assigned request. No application path can
    // reach the state below; forcing the row by hand is the only way to give the requeue guard
    // a test, and without one the guard could be deleted with every test still green.
    [Fact]
    public async Task SweepAsync_WhenTheRequestIsNoLongerAssigned_ExpiresTheAssignmentWithoutRequeueing()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);

        await using (var forcedDb = _fixture.NewDbContext())
        {
            await forcedDb.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_requests
                   SET status = 'Completed', completed_at = now()
                 WHERE id = {0}
                """,
                [assignment.RequestId],
                TestContext.Current.CancellationToken);
            await forcedDb.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_assignments
                   SET leased_until = now() - INTERVAL '1 minute'
                 WHERE id = {0}
                """,
                [assignment.AssignmentId],
                TestContext.Current.CancellationToken);
        }

        ReviewLeaseSweepResult result;
        await using (var sweepDb = _fixture.NewDbContext())
        {
            result = await NewSweepService(sweepDb).SweepAsync();
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

        // The guard stops only the requeue, so the expired lease is still released.
        Assert.Equal(1, result.ExpiredCount);
        Assert.Equal(ReviewAssignmentStatus.Expired, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Completed, requestStatus);
    }

    [Fact]
    public async Task SweepAsync_WhenASubmissionCommitsFirst_DoesNotExpireTheAssignment()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        DateTimeOffset leasedUntil;
        int creditBefore;

        await using (var leaseDb = _fixture.NewDbContext())
        {
            await leaseDb.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_assignments
                   SET leased_until = now() + INTERVAL '500 milliseconds'
                 WHERE id = {0}
                """,
                [assignment.AssignmentId],
                TestContext.Current.CancellationToken);
            leasedUntil = await leaseDb.ReviewAssignments
                .Where(item => item.Id == assignment.AssignmentId)
                .Select(item => item.LeasedUntil)
                .SingleAsync(TestContext.Current.CancellationToken);
            creditBefore = await leaseDb.Users
                .Where(user => user.Id == assignment.ReviewerId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        await using var submissionDb = _fixture.NewDbContext();
        await submissionDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var submissionTransaction = await submissionDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var submissionResult = await NewSubmissionService(submissionDb).SubmitAsync(
            assignment.AssignmentId,
            assignment.ReviewerId,
            "This submission started before its lease reached the deadline.");
        Assert.Equal(FeedbackSubmissionOutcome.Submitted, submissionResult.Outcome);

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        await using var sweepDb = _fixture.NewDbContext();
        await sweepDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // A broken conflict path must fail instead of waiting forever on the submission.
        await sweepDb.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '5s'",
            TestContext.Current.CancellationToken);
        await using var sweepTransaction = await sweepDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var sweepClockIsAfterDeadline = await sweepDb.Database
            .SqlQueryRaw<bool>(
                "SELECT now() >= @leasedUntil AS \"Value\"",
                new NpgsqlParameter("leasedUntil", leasedUntil))
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(
            sweepClockIsAfterDeadline,
            "The sweep transaction did not start after the lease deadline.");

        var sweepBackendId = ((NpgsqlConnection)sweepDb.Database.GetDbConnection()).ProcessID;
        var sweepTask = NewSweepService(sweepDb).SweepAsync();

        // Seeing PostgreSQL report B's lock wait proves its stale eligible row reached A's lock.
        await using (var observerDb = _fixture.NewDbContext())
        {
            var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(6);

            while (true)
            {
                var sweepIsWaiting = await observerDb.Database
                    .SqlQueryRaw<bool>(
                        """
                        SELECT EXISTS (
                            SELECT 1
                              FROM pg_stat_activity
                             WHERE pid = @backendId
                               AND wait_event_type = 'Lock'
                        ) AS "Value"
                        """,
                        new NpgsqlParameter("backendId", sweepBackendId))
                    .SingleAsync(TestContext.Current.CancellationToken);

                if (sweepIsWaiting)
                {
                    break;
                }

                if (sweepTask.IsCompleted)
                {
                    var prematureResult = await sweepTask;
                    Assert.Fail(
                        $"The sweep completed before reaching the forced conflict: " +
                        $"expired={prematureResult.ExpiredCount}.");
                }

                Assert.True(
                    DateTimeOffset.UtcNow < waitDeadline,
                    "The sweep did not reach a PostgreSQL lock wait within six seconds.");
                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    TestContext.Current.CancellationToken);
            }
        }

        await submissionTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var sweepResult = await sweepTask;
        await sweepTransaction.CommitAsync(TestContext.Current.CancellationToken);

        await using var verificationDb = _fixture.NewDbContext();
        var assignmentStatus = await verificationDb.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var requestStatus = await verificationDb.ReviewRequests
            .Where(item => item.Id == assignment.RequestId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var reviewerCredit = await verificationDb.Users
            .Where(user => user.Id == assignment.ReviewerId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, sweepResult.ExpiredCount);
        Assert.Equal(ReviewAssignmentStatus.Completed, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Completed, requestStatus);
        Assert.Equal(creditBefore + 1, reviewerCredit);
    }

    [Fact]
    public async Task SubmitAsync_WhenASweepCommitsFirst_IsRefusedAsExpired()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        DateTimeOffset leasedUntil;
        int creditBefore;

        await using (var leaseDb = _fixture.NewDbContext())
        {
            await leaseDb.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_assignments
                   SET leased_until = now() + INTERVAL '500 milliseconds'
                 WHERE id = {0}
                """,
                [assignment.AssignmentId],
                TestContext.Current.CancellationToken);
            leasedUntil = await leaseDb.ReviewAssignments
                .Where(item => item.Id == assignment.AssignmentId)
                .Select(item => item.LeasedUntil)
                .SingleAsync(TestContext.Current.CancellationToken);
            creditBefore = await leaseDb.Users
                .Where(user => user.Id == assignment.ReviewerId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        await using var submissionDb = _fixture.NewDbContext();
        await submissionDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await submissionDb.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '5s'",
            TestContext.Current.CancellationToken);
        await using var submissionTransaction = await submissionDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);

        // Executing inside A fixes now() before the deadline while leaving the assignment unlocked.
        await submissionDb.Database
            .SqlQueryRaw<int>("SELECT 1 AS \"Value\"")
            .SingleAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        var submissionClockIsBeforeDeadline = await submissionDb.Database
            .SqlQueryRaw<bool>(
                "SELECT now() < @leasedUntil AS \"Value\"",
                new NpgsqlParameter("leasedUntil", leasedUntil))
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(
            submissionClockIsBeforeDeadline,
            "The submission transaction did not preserve its pre-deadline clock.");

        ReviewLeaseSweepResult sweepResult;
        await using (var sweepDb = _fixture.NewDbContext())
        {
            await sweepDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await sweepDb.Database.ExecuteSqlRawAsync(
                "SET lock_timeout = '5s'",
                TestContext.Current.CancellationToken);
            await using var sweepTransaction = await sweepDb.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            sweepResult = await NewSweepService(sweepDb).SweepAsync();
            Assert.Equal(1, sweepResult.ExpiredCount);
            await sweepTransaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var submissionResult = await NewSubmissionService(submissionDb).SubmitAsync(
            assignment.AssignmentId,
            assignment.ReviewerId,
            "The stale submission clock must not defeat the completed sweep.");
        await submissionTransaction.CommitAsync(TestContext.Current.CancellationToken);

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
        var earnEntryCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == assignment.ReviewerId &&
                     entry.Kind == CreditEntryKind.Earn,
            TestContext.Current.CancellationToken);
        var reviewerCredit = await verificationDb.Users
            .Where(user => user.Id == assignment.ReviewerId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FeedbackSubmissionOutcome.LeaseExpired, submissionResult.Outcome);
        Assert.Equal(ReviewAssignmentStatus.Expired, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Queued, requestStatus);
        Assert.Equal(0, submissionCount);
        Assert.Equal(0, earnEntryCount);
        Assert.Equal(creditBefore, reviewerCredit);
    }

    private static ReviewLeaseSweepService NewSweepService(AppDbContext db) =>
        new(db, NullLogger<ReviewLeaseSweepService>.Instance);

    private static FeedbackSubmissionService NewSubmissionService(AppDbContext db) =>
        new(
            db,
            NullLogger<FeedbackSubmissionService>.Instance,
            new TestNotificationEventPublisher());
}
