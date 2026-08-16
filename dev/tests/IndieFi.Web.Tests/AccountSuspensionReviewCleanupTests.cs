using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Feedback.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountSuspensionReviewCleanupTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 14, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AccountSuspensionReviewCleanupTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendAsync_ExpiresEveryActiveAssignmentAndPreservesQueueOrder(
        bool temporarily)
    {
        var arranged = await CreateTwoActiveAssignmentsAsync();

        AccountStatusTransitionResult result;
        await using (var transitionDb = _fixture.NewDbContext())
        {
            var service = NewTransitionService(transitionDb);
            result = temporarily
                ? await service.SuspendTemporarilyAsync(
                    arranged.ReviewerId,
                    Now.AddDays(1),
                    TestContext.Current.CancellationToken)
                : await service.SuspendIndefinitelyAsync(
                    arranged.ReviewerId,
                    TestContext.Current.CancellationToken);
        }

        Assert.Equal(AccountStatusTransitionOutcome.Changed, result.Outcome);
        Assert.Equal(2, result.ExpiredReviewAssignmentCount);

        await using var verificationDb = _fixture.NewDbContext();
        var assignments = await verificationDb.ReviewAssignments
            .AsNoTracking()
            .Where(item => item.ReviewerId == arranged.ReviewerId)
            .OrderBy(item => item.RequestId)
            .Select(item => new { item.RequestId, item.Status })
            .ToListAsync(TestContext.Current.CancellationToken);
        var requests = await verificationDb.ReviewRequests
            .AsNoTracking()
            .Where(item => arranged.RequestIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Status, item.CreatedAt })
            .ToListAsync(TestContext.Current.CancellationToken);
        var ledgerCount = await verificationDb.CreditLedgerEntries.CountAsync(
            TestContext.Current.CancellationToken);
        var gameIds = await verificationDb.Games
            .AsNoTracking()
            .Where(game => arranged.GameIds.Contains(game.Id))
            .OrderBy(game => game.Id)
            .Select(game => game.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(
            assignments,
            assignment => Assert.Equal(ReviewAssignmentStatus.Expired, assignment.Status));
        Assert.All(
            requests,
            request => Assert.Equal(ReviewRequestStatus.Queued, request.Status));
        Assert.Equal(arranged.RequestIds, requests.Select(request => request.Id));
        Assert.Equal(arranged.RequestCreatedAt, requests.Select(request => request.CreatedAt));
        Assert.Equal(arranged.LedgerCount, ledgerCount);
        Assert.Equal(arranged.GameIds, gameIds);
        Assert.Equal(
            arranged.CreditBalances,
            await LoadCreditBalancesAsync(verificationDb, arranged.UserIds));
    }

    [Fact]
    public async Task SuspendAsync_LeavesCompletedReviewAndEarnedCreditUnchanged()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using (var submissionDb = _fixture.NewDbContext())
        {
            var submitted = await new FeedbackSubmissionService(
                    submissionDb,
                    NullLogger<FeedbackSubmissionService>.Instance)
                .SubmitAsync(
                    assignment.AssignmentId,
                    assignment.ReviewerId,
                    "A completed review must remain visible after account suspension.");
            Assert.Equal(FeedbackSubmissionOutcome.Submitted, submitted.Outcome);
        }

        int creditBefore;
        int ledgerCountBefore;
        await using (var beforeDb = _fixture.NewDbContext())
        {
            creditBefore = await beforeDb.Users
                .Where(user => user.Id == assignment.ReviewerId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);
            ledgerCountBefore = await beforeDb.CreditLedgerEntries.CountAsync(
                TestContext.Current.CancellationToken);
        }

        await using (var transitionDb = _fixture.NewDbContext())
        {
            var result = await NewTransitionService(transitionDb)
                .SuspendIndefinitelyAsync(
                    assignment.ReviewerId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(AccountStatusTransitionOutcome.Changed, result.Outcome);
            Assert.Equal(0, result.ExpiredReviewAssignmentCount);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            ReviewAssignmentStatus.Completed,
            await verificationDb.ReviewAssignments
                .Where(item => item.Id == assignment.AssignmentId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ReviewRequestStatus.Completed,
            await verificationDb.ReviewRequests
                .Where(item => item.Id == assignment.RequestId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await verificationDb.FeedbackSubmissions.CountAsync(
                item => item.AssignmentId == assignment.AssignmentId,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            creditBefore,
            await verificationDb.Users
                .Where(user => user.Id == assignment.ReviewerId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ledgerCountBefore,
            await verificationDb.CreditLedgerEntries.CountAsync(
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuspendAsync_WhenRequestIsNotAssigned_ExpiresOnlyTheAssignment()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using (var forcedDb = _fixture.NewDbContext())
        {
            await forcedDb.ReviewRequests
                .Where(request => request.Id == assignment.RequestId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            request => request.Status,
                            ReviewRequestStatus.Completed)
                        .SetProperty(
                            request => request.CompletedAt,
                            DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        await using (var transitionDb = _fixture.NewDbContext())
        {
            var result = await NewTransitionService(transitionDb)
                .SuspendIndefinitelyAsync(
                    assignment.ReviewerId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(1, result.ExpiredReviewAssignmentCount);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            ReviewAssignmentStatus.Expired,
            await verificationDb.ReviewAssignments
                .Where(item => item.Id == assignment.AssignmentId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            ReviewRequestStatus.Completed,
            await verificationDb.ReviewRequests
                .Where(item => item.Id == assignment.RequestId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    private async Task<ArrangedReviews> CreateTwoActiveAssignmentsAsync()
    {
        long reviewerId;
        var requestIds = new List<long>();
        var requestCreatedAt = new List<DateTimeOffset>();
        var gameIds = new List<long>();
        var userIds = new List<long>();

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(
                TestContext.Current.CancellationToken);
            var reviewer = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0);
            reviewerId = reviewer.Id;
            userIds.Add(reviewerId);

            for (var index = 0; index < 2; index++)
            {
                var requester = await ReviewTestData.CreateUserWithCreditAsync(
                    arrangeDb,
                    balance: 1);
                var game = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
                var request = await ReviewTestData.CreateQueuedRequestAsync(
                    arrangeDb,
                    requester.Id,
                    game.Id);
                userIds.Add(requester.Id);
                gameIds.Add(game.Id);
                requestIds.Add(request.Id);
                requestCreatedAt.Add(request.CreatedAt);
            }
        }

        await using (var claimDb = _fixture.NewDbContext())
        {
            var service = new ReviewClaimService(
                claimDb,
                NullLogger<ReviewClaimService>.Instance,
                new TestNotificationEventPublisher());
            for (var index = 0; index < 2; index++)
            {
                var result = await service.ClaimNextAsync(reviewerId);
                Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
                Assert.Equal(requestIds[index], result.RequestId);
            }
        }

        await using var snapshotDb = _fixture.NewDbContext();
        return new ArrangedReviews(
            reviewerId,
            requestIds,
            requestCreatedAt,
            gameIds,
            userIds,
            await snapshotDb.CreditLedgerEntries.CountAsync(
                TestContext.Current.CancellationToken),
            await LoadCreditBalancesAsync(snapshotDb, userIds));
    }

    private static AccountStatusTransitionService NewTransitionService(AppDbContext db)
    {
        return new AccountStatusTransitionService(db, new FixedTimeProvider(Now));
    }

    private static async Task<List<int>> LoadCreditBalancesAsync(
        AppDbContext db,
        IReadOnlyCollection<long> userIds)
    {
        return await db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .OrderBy(user => user.Id)
            .Select(user => user.CreditBalance)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private sealed record ArrangedReviews(
        long ReviewerId,
        IReadOnlyList<long> RequestIds,
        IReadOnlyList<DateTimeOffset> RequestCreatedAt,
        IReadOnlyList<long> GameIds,
        IReadOnlyList<long> UserIds,
        int LedgerCount,
        IReadOnlyList<int> CreditBalances);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
