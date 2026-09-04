using System.Net;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountStatusSecurityFlowTests
{
    private readonly PostgreSqlFixture _fixture;

    public AccountStatusSecurityFlowTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Suspension_RevokesTwoBrowserSessionsAndRequeuesReviewWithoutChangingData()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture,
            sessionVersion: 17);
        var arranged = await ArrangeActiveReviewForEmailAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(firstClient, email, password);
        await PageTestSupport.LoginAsync(secondClient, email, password);

        AccountStatusTransitionResult suspension;
        await using (var transitionDb = _fixture.NewDbContext())
        {
            suspension = await new AccountStatusTransitionService(
                    transitionDb,
                    TimeProvider.System)
                .SuspendIndefinitelyAsync(
                    arranged.ReviewerId,
                    TestContext.Current.CancellationToken);
        }

        using var firstResponse = await firstClient.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        using var secondResponse = await secondClient.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountStatusTransitionOutcome.Changed, suspension.Outcome);
        Assert.Equal(1, suspension.ExpiredReviewAssignmentCount);
        AssertLoginChallenge(firstResponse);
        AssertLoginChallenge(secondResponse);

        await using var verificationDb = _fixture.NewDbContext();
        var reviewer = await verificationDb.Users.SingleAsync(
            item => item.Id == arranged.ReviewerId,
            TestContext.Current.CancellationToken);
        Assert.Equal(AccountStatus.IndefinitelySuspended, reviewer.AccountStatus);
        Assert.Equal(18, reviewer.SessionVersion);
        Assert.Equal(arranged.CreditBalance, reviewer.CreditBalance);
        Assert.Equal(
            arranged.LedgerCount,
            await verificationDb.CreditLedgerEntries.CountAsync(
                item => item.UserId == arranged.ReviewerId,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            ReviewAssignmentStatus.Expired,
            await verificationDb.ReviewAssignments
                .Where(item => item.Id == arranged.AssignmentId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ReviewRequestStatus.Queued,
            await verificationDb.ReviewRequests
                .Where(item => item.Id == arranged.RequestId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            arranged.GameTitle,
            await verificationDb.Games
                .Where(item => item.Id == arranged.GameId)
                .Select(item => item.Title)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredTemporarySuspension_IssuesOnlyNewSessionAndRejectsOldCookies()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture,
            sessionVersion: 20);
        var userId = await FindUserIdAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var firstOldClient = factory.CreateClient();
        using var secondOldClient = factory.CreateClient();
        using var blockedLoginClient = factory.CreateClient();
        using var newClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(firstOldClient, email, password);
        await PageTestSupport.LoginAsync(secondOldClient, email, password);

        await using (var transitionDb = _fixture.NewDbContext())
        {
            var result = await new AccountStatusTransitionService(
                    transitionDb,
                    TimeProvider.System)
                .SuspendTemporarilyAsync(
                    userId,
                    DateTimeOffset.UtcNow.AddHours(1),
                    TestContext.Current.CancellationToken);
            Assert.Equal(AccountStatusTransitionOutcome.Changed, result.Outcome);
            Assert.Equal(21, result.SessionVersion);
        }

        using (var blockedLogin = await PostLoginAsync(
                   blockedLoginClient,
                   email,
                   password))
        {
            Assert.Equal(HttpStatusCode.OK, blockedLogin.StatusCode);
            if (blockedLogin.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                Assert.DoesNotContain(
                    setCookies,
                    value => value.StartsWith(
                        ".AspNetCore.Cookies=",
                        StringComparison.Ordinal));
            }
        }

        await using (var expiryDb = _fixture.NewDbContext())
        {
            var user = await expiryDb.Users.SingleAsync(
                item => item.Id == userId,
                TestContext.Current.CancellationToken);
            user.SuspendedUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            await expiryDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await PageTestSupport.LoginAsync(newClient, email, password);

        using var firstOldResponse = await firstOldClient.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        using var secondOldResponse = await secondOldClient.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        using var newResponse = await newClient.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);

        AssertLoginChallenge(firstOldResponse);
        AssertLoginChallenge(secondOldResponse);
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);

        await using var verificationDb = _fixture.NewDbContext();
        var reactivated = await verificationDb.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(AccountStatus.Active, reactivated.AccountStatus);
        Assert.Null(reactivated.SuspendedUntil);
        Assert.Equal(22, reactivated.SessionVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginAndSuspensionRace_LeavesNoValidPreSuspensionSession(
        bool suspensionStartsFirst)
    {
        const long initialSessionVersion = 31;
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture,
            sessionVersion: initialSessionVersion);
        var userId = await FindUserIdAsync(email);

        await using var blockerDb = _fixture.NewDbContext();
        await blockerDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await LockUserAsync(blockerDb, userId);

        await using var authenticationDb = _fixture.NewDbContext();
        await using var suspensionDb = _fixture.NewDbContext();
        await PrepareForLockTestAsync(authenticationDb);
        await PrepareForLockTestAsync(suspensionDb);
        var authenticationBackendId =
            ((NpgsqlConnection)authenticationDb.Database.GetDbConnection()).ProcessID;
        var suspensionBackendId =
            ((NpgsqlConnection)suspensionDb.Database.GetDbConnection()).ProcessID;
        var authenticationService = new AccountAuthenticationService(
            authenticationDb,
            new PasswordHasher<User>(),
            TestAccountProtection.CreateService(),
            TimeProvider.System);
        var suspensionService = new AccountStatusTransitionService(
            suspensionDb,
            TimeProvider.System);

        Task<AccountAuthenticationResult> authenticationTask;
        Task<AccountStatusTransitionResult> suspensionTask;
        if (suspensionStartsFirst)
        {
            suspensionTask = suspensionService.SuspendIndefinitelyAsync(
                userId,
                TestContext.Current.CancellationToken);
            await AssertBackendWaitsForLockAsync(suspensionBackendId, suspensionTask);
            authenticationTask = authenticationService.AuthenticateAsync(
                email,
                password,
                TestContext.Current.CancellationToken);
            await AssertBackendWaitsForLockAsync(
                authenticationBackendId,
                authenticationTask);
        }
        else
        {
            authenticationTask = authenticationService.AuthenticateAsync(
                email,
                password,
                TestContext.Current.CancellationToken);
            await AssertBackendWaitsForLockAsync(
                authenticationBackendId,
                authenticationTask);
            suspensionTask = suspensionService.SuspendIndefinitelyAsync(
                userId,
                TestContext.Current.CancellationToken);
            await AssertBackendWaitsForLockAsync(suspensionBackendId, suspensionTask);
        }

        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var authentication = await authenticationTask;
        var suspension = await suspensionTask;

        Assert.Equal(AccountStatusTransitionOutcome.Changed, suspension.Outcome);
        Assert.Equal(32, suspension.SessionVersion);
        Assert.Equal(
            suspensionStartsFirst
                ? AccountAuthenticationOutcome.AccountUnavailable
                : AccountAuthenticationOutcome.Authenticated,
            authentication.Outcome);

        await using var validationDb = _fixture.NewDbContext();
        Assert.False(await new AccountSessionValidationService(validationDb).IsValidAsync(
            userId,
            initialSessionVersion,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FeedbackSubmissionAndSuspensionRace_HasOneConsistentWinner(
        bool suspensionStartsFirst)
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        var snapshot = await LoadReviewSnapshotAsync(assignment);

        await using var blockerDb = _fixture.NewDbContext();
        await blockerDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await LockUserAsync(blockerDb, assignment.ReviewerId);

        await using var submissionDb = _fixture.NewDbContext();
        await using var suspensionDb = _fixture.NewDbContext();
        await PrepareForLockTestAsync(submissionDb);
        await PrepareForLockTestAsync(suspensionDb);
        var submissionBackendId =
            ((NpgsqlConnection)submissionDb.Database.GetDbConnection()).ProcessID;
        var suspensionBackendId =
            ((NpgsqlConnection)suspensionDb.Database.GetDbConnection()).ProcessID;
        var submissionService = new FeedbackSubmissionService(
            submissionDb,
            NullLogger<FeedbackSubmissionService>.Instance,
            new TestNotificationEventPublisher());
        var suspensionService = new AccountStatusTransitionService(
            suspensionDb,
            TimeProvider.System);

        Task<FeedbackSubmissionResult> submissionTask;
        Task<AccountStatusTransitionResult> suspensionTask;
        if (suspensionStartsFirst)
        {
            suspensionTask = suspensionService.SuspendIndefinitelyAsync(
                assignment.ReviewerId,
                TestContext.Current.CancellationToken);
            await AssertBackendWaitsForLockAsync(suspensionBackendId, suspensionTask);
            submissionTask = submissionService.SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                "A competing feedback submission must either fully win or fully lose.");
            await AssertBackendWaitsForLockAsync(submissionBackendId, submissionTask);
        }
        else
        {
            submissionTask = submissionService.SubmitAsync(
                assignment.AssignmentId,
                assignment.ReviewerId,
                "A competing feedback submission must either fully win or fully lose.");
            await AssertBackendWaitsForLockAsync(submissionBackendId, submissionTask);
            suspensionTask = suspensionService.SuspendIndefinitelyAsync(
                assignment.ReviewerId,
                TestContext.Current.CancellationToken);
            await AssertBackendWaitsForLockAsync(suspensionBackendId, suspensionTask);
        }

        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var submission = await submissionTask;
        var suspension = await suspensionTask;

        Assert.Equal(AccountStatusTransitionOutcome.Changed, suspension.Outcome);
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
        var earnCount = await verificationDb.CreditLedgerEntries.CountAsync(
            item => item.UserId == assignment.ReviewerId
                && item.Kind == CreditEntryKind.Earn,
            TestContext.Current.CancellationToken);
        var creditBalance = await verificationDb.Users
            .Where(item => item.Id == assignment.ReviewerId)
            .Select(item => item.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        if (suspensionStartsFirst)
        {
            Assert.Equal(FeedbackSubmissionOutcome.AccountUnavailable, submission.Outcome);
            Assert.Equal(1, suspension.ExpiredReviewAssignmentCount);
            Assert.Equal(ReviewAssignmentStatus.Expired, assignmentStatus);
            Assert.Equal(ReviewRequestStatus.Queued, requestStatus);
            Assert.Equal(0, submissionCount);
            Assert.Equal(snapshot.EarnCount, earnCount);
            Assert.Equal(snapshot.CreditBalance, creditBalance);
        }
        else
        {
            Assert.Equal(FeedbackSubmissionOutcome.Submitted, submission.Outcome);
            Assert.Equal(0, suspension.ExpiredReviewAssignmentCount);
            Assert.Equal(ReviewAssignmentStatus.Completed, assignmentStatus);
            Assert.Equal(ReviewRequestStatus.Completed, requestStatus);
            Assert.Equal(1, submissionCount);
            Assert.Equal(snapshot.EarnCount + 1, earnCount);
            Assert.Equal(snapshot.CreditBalance + 1, creditBalance);
        }
    }

    private async Task<ArrangedReview> ArrangeActiveReviewForEmailAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        var reviewer = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        var requester = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
        var game = await ReviewTestData.CreateGameAsync(db, requester.Id);
        game.Title = $"Preserved game {Guid.NewGuid():N}";
        var request = await ReviewTestData.CreateQueuedRequestAsync(
            db,
            requester.Id,
            game.Id);
        request.Status = ReviewRequestStatus.Assigned;
        var assignment = new ReviewAssignment
        {
            RequestId = request.Id,
            GameId = game.Id,
            ReviewerId = reviewer.Id,
            ClaimOperationId = Guid.NewGuid(),
            Status = ReviewAssignmentStatus.Active,
            LeasedUntil = DateTimeOffset.UtcNow.AddMinutes(30),
        };
        db.ReviewAssignments.Add(assignment);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new ArrangedReview(
            reviewer.Id,
            request.Id,
            assignment.Id,
            game.Id,
            game.Title,
            await db.Users
                .Where(item => item.Id == reviewer.Id)
                .Select(item => item.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken),
            await db.CreditLedgerEntries.CountAsync(
                item => item.UserId == reviewer.Id,
                TestContext.Current.CancellationToken));
    }

    private async Task<long> FindUserIdAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        return (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client,
        string email,
        string password)
    {
        using var pageResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        return await client.PostAsync(
            "/Login",
            form,
            TestContext.Current.CancellationToken);
    }

    private static void AssertLoginChallenge(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.AbsolutePath);
    }

    private static async Task LockUserAsync(AppDbContext db, long userId)
    {
        await db.Database
            .SqlQueryRaw<long>(
                "SELECT id AS \"Value\" FROM users WHERE id = @userId FOR UPDATE",
                new NpgsqlParameter("userId", userId))
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task PrepareForLockTestAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '5s'",
            TestContext.Current.CancellationToken);
    }

    private async Task AssertBackendWaitsForLockAsync(int backendId, Task task)
    {
        await using var observerDb = _fixture.NewDbContext();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (true)
        {
            var isWaiting = await observerDb.Database
                .SqlQueryRaw<bool>(
                    """
                    SELECT EXISTS (
                        SELECT 1
                          FROM pg_stat_activity
                         WHERE pid = @backendId
                           AND wait_event_type = 'Lock'
                    ) AS "Value"
                    """,
                    new NpgsqlParameter("backendId", backendId))
                .SingleAsync(TestContext.Current.CancellationToken);
            if (isWaiting)
            {
                return;
            }

            Assert.False(
                task.IsCompleted,
                "The competing operation completed before reaching the forced row lock.");
            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "The competing operation did not reach a row lock within three seconds.");
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }
    }

    private async Task<ReviewSnapshot> LoadReviewSnapshotAsync(
        ReviewTestData.LiveAssignment assignment)
    {
        await using var db = _fixture.NewDbContext();
        return new ReviewSnapshot(
            await db.Users
                .Where(item => item.Id == assignment.ReviewerId)
                .Select(item => item.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken),
            await db.CreditLedgerEntries.CountAsync(
                item => item.UserId == assignment.ReviewerId
                    && item.Kind == CreditEntryKind.Earn,
                TestContext.Current.CancellationToken));
    }

    private sealed record ArrangedReview(
        long ReviewerId,
        long RequestId,
        long AssignmentId,
        long GameId,
        string GameTitle,
        int CreditBalance,
        int LedgerCount);

    private sealed record ReviewSnapshot(int CreditBalance, int EarnCount);
}
