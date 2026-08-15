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
public sealed class ReviewRequestServiceTests
{
    private const string UniqueViolation = "23505";
    private const string RefundEntryIndex = "IX_review_requests_refund_entry_id";

    private readonly PostgreSqlFixture _fixture;

    public ReviewRequestServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RequestAsync_WhenBalanceIsEnough_SpendsOneCreditAndQueuesTheRequest()
    {
        long userId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var arrangedUser = await CreateUserWithCreditAsync(arrangeDb, balance: 3);
            userId = arrangedUser.Id;
            gameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).RequestAsync(gameId, userId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            user => user.Id == userId,
            TestContext.Current.CancellationToken);
        var spendEntries = await verificationDb.CreditLedgerEntries
            .Where(entry =>
                entry.UserId == userId &&
                entry.Kind == CreditEntryKind.Spend)
            .ToListAsync(TestContext.Current.CancellationToken);
        var requests = await verificationDb.ReviewRequests
            .Where(request => request.RequesterId == userId && request.GameId == gameId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var ledgerSum = await LedgerSumAsync(verificationDb, userId);

        Assert.Equal(ReviewRequestOutcome.Queued, result.Outcome);
        Assert.Equal(2, user.CreditBalance);
        var spendEntry = Assert.Single(spendEntries);
        Assert.Equal(-1, spendEntry.Delta);
        var request = Assert.Single(requests);
        Assert.Equal(userId, request.RequesterId);
        Assert.Equal(gameId, request.GameId);
        Assert.Equal(spendEntry.Id, request.SpendEntryId);
        Assert.Equal(user.CreditBalance, ledgerSum);
    }

    [Fact]
    public async Task RequestAsync_WhenBalanceIsZero_WritesNothing()
    {
        long userId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 0);
            userId = user.Id;
            gameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).RequestAsync(gameId, userId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var spendCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == userId && entry.Kind == CreditEntryKind.Spend,
            TestContext.Current.CancellationToken);
        var requestCount = await verificationDb.ReviewRequests.CountAsync(
            request => request.RequesterId == userId && request.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.InsufficientCredit, result.Outcome);

        // The result alone cannot prove that the debit was rolled back. Leaving any
        // failure-path trace would make the cached balance diverge from the ledger.
        Assert.Equal(0, balance);
        Assert.Equal(0, spendCount);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task RequestAsync_WhenGameBelongsToSomeoneElse_WritesNothing()
    {
        long requesterId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            requesterId = (await CreateUserWithCreditAsync(arrangeDb, balance: 3)).Id;
            var owner = await CreateUserWithCreditAsync(arrangeDb, balance: 0);
            gameId = (await CreateGameAsync(arrangeDb, owner.Id)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).RequestAsync(gameId, requesterId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var balance = await verificationDb.Users
            .Where(user => user.Id == requesterId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var spendCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == requesterId && entry.Kind == CreditEntryKind.Spend,
            TestContext.Current.CancellationToken);
        var requestCount = await verificationDb.ReviewRequests.CountAsync(
            request => request.RequesterId == requesterId && request.GameId == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.NotOwner, result.Outcome);
        Assert.Equal(3, balance);
        Assert.Equal(0, spendCount);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task RequestAsync_WhenOneCreditIsRequestedManyTimesAtOnce_SpendsItExactlyOnce()
    {
        const int RequestCount = 8;
        long userId;
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            userId = user.Id;
            gameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        // Fire all eight requests on separate connections. With one credit, a read-then-write
        // implementation would let callers pass the same stale check; putting the check in the
        // UPDATE WHERE makes check-and-debit atomic, so exactly one UPDATE can affect the row.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, RequestCount).Select(async _ =>
        {
            await using var serviceDb = _fixture.NewDbContext();
            return (await NewService(serviceDb).RequestAsync(gameId, userId)).Outcome;
        }));

        await using var verificationDb = _fixture.NewDbContext();
        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var spendEntries = await verificationDb.CreditLedgerEntries
            .Where(entry =>
                entry.UserId == userId &&
                entry.Kind == CreditEntryKind.Spend)
            .ToListAsync(TestContext.Current.CancellationToken);
        var requests = await verificationDb.ReviewRequests
            .Where(request => request.RequesterId == userId && request.GameId == gameId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var ledgerSum = await LedgerSumAsync(verificationDb, userId);

        Assert.Equal(1, outcomes.Count(outcome => outcome == ReviewRequestOutcome.Queued));
        Assert.Equal(
            RequestCount - 1,
            outcomes.Count(outcome => outcome == ReviewRequestOutcome.InsufficientCredit));
        Assert.Equal(0, balance);
        Assert.Single(spendEntries);
        Assert.Single(requests);
        Assert.Equal(balance, ledgerSum);
    }

    [Fact]
    public async Task CancelAsync_WhenTheQueuedRequestBelongsToTheUser_RefundsOneCredit()
    {
        long userId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            userId = user.Id;
            var game = await CreateGameAsync(arrangeDb, userId);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                userId,
                game.Id)).Id;
        }

        var balanceBefore = await BalanceAsync(userId);

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).CancelAsync(requestId, userId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var requestState = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => new
            {
                request.Status,
                request.CompletedAt,
                request.CancelledAt,
            })
            .SingleAsync(TestContext.Current.CancellationToken);
        var refundEntries = await verificationDb.CreditLedgerEntries
            .Where(entry => entry.UserId == userId && entry.Kind == CreditEntryKind.Refund)
            .ToListAsync(TestContext.Current.CancellationToken);
        var balanceAfter = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.Cancelled, result.Outcome);
        Assert.Equal(ReviewRequestStatus.Cancelled, requestState.Status);
        Assert.Null(requestState.CompletedAt);
        Assert.NotNull(requestState.CancelledAt);
        var refundEntry = Assert.Single(refundEntries);
        Assert.Equal(1, refundEntry.Delta);
        Assert.Equal(balanceBefore + 1, balanceAfter);
    }

    [Fact]
    public async Task CancelAsync_WhenTheRequestBelongsToSomeoneElse_WritesNothing()
    {
        long requesterId;
        long otherUserId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            requesterId = requester.Id;
            otherUserId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requesterId);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requesterId,
                game.Id)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).CancelAsync(requestId, otherUserId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var requestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var refundCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry =>
                entry.Kind == CreditEntryKind.Refund
                && (entry.UserId == requesterId || entry.UserId == otherUserId),
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.NotCancellable, result.Outcome);
        Assert.Equal(ReviewRequestStatus.Queued, requestStatus);
        Assert.Equal(0, refundCount);
    }

    [Fact]
    public async Task CancelAsync_WhenTheRequestIsAssigned_WritesNothing()
    {
        long requesterId;
        long reviewerId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            requesterId = requester.Id;
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requesterId);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requesterId,
                game.Id)).Id;
        }

        await using (var claimDb = _fixture.NewDbContext())
        {
            var claimResult = await NewClaimService(claimDb).ClaimNextAsync(reviewerId);
            Assert.Equal(ReviewClaimOutcome.Claimed, claimResult.Outcome);
            Assert.Equal(requestId, claimResult.RequestId);
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).CancelAsync(requestId, requesterId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var requestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var refundCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == requesterId && entry.Kind == CreditEntryKind.Refund,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.NotCancellable, result.Outcome);
        Assert.Equal(ReviewRequestStatus.Assigned, requestStatus);
        Assert.Equal(0, refundCount);
    }

    [Fact]
    public async Task CancelAsync_WhenTheSameRequestIsCancelledTwice_RefundsExactlyOnce()
    {
        long userId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            userId = user.Id;
            var game = await CreateGameAsync(arrangeDb, userId);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                userId,
                game.Id)).Id;
        }

        ReviewRequestResult firstResult;
        ReviewRequestResult secondResult;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            var service = NewService(serviceDb);
            firstResult = await service.CancelAsync(requestId, userId);
            secondResult = await service.CancelAsync(requestId, userId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var refundEntries = await verificationDb.CreditLedgerEntries
            .Where(entry => entry.UserId == userId && entry.Kind == CreditEntryKind.Refund)
            .ToListAsync(TestContext.Current.CancellationToken);
        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.Cancelled, firstResult.Outcome);
        Assert.Equal(ReviewRequestOutcome.Cancelled, secondResult.Outcome);
        Assert.Single(refundEntries);
        Assert.Equal(1, balance);
    }

    [Fact]
    public async Task CancelAsync_WhenTheRequestDoesNotExist_ReturnsNotCancellable()
    {
        long userId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
        }

        ReviewRequestResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).CancelAsync(long.MaxValue, userId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var refundCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == userId && entry.Kind == CreditEntryKind.Refund,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.NotCancellable, result.Outcome);
        Assert.Equal(0, refundCount);
    }

    [Fact]
    public async Task CancelAsync_WhenCancellationSucceeds_StoresTheRefundEntryId()
    {
        long userId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            userId = user.Id;
            var game = await CreateGameAsync(arrangeDb, userId);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                userId,
                game.Id)).Id;
        }

        await using (var serviceDb = _fixture.NewDbContext())
        {
            var result = await NewService(serviceDb).CancelAsync(requestId, userId);
            Assert.Equal(ReviewRequestOutcome.Cancelled, result.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var refundEntryId = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.RefundEntryId)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(refundEntryId);
        var refundEntry = await verificationDb.CreditLedgerEntries.SingleAsync(
            entry => entry.Id == refundEntryId,
            TestContext.Current.CancellationToken);
        Assert.Equal(userId, refundEntry.UserId);
        Assert.Equal(CreditEntryKind.Refund, refundEntry.Kind);
        Assert.Equal(1, refundEntry.Delta);
    }

    // Cancellation always inserts a fresh ledger row and attaches it to the one request it
    // locked, so no product path can point two requests at the same refund. The unique index
    // guards a future write path instead, which only a deliberate violation can demonstrate:
    // without it, a second request could claim a refund that was already paid out once.
    [Fact]
    public async Task ReusingARefundEntryOnASecondRequest_IsRejectedByTheDatabase()
    {
        long userId;
        long cancelledRequestId;
        long otherRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            userId = user.Id;
            var game = await CreateGameAsync(arrangeDb, userId);
            cancelledRequestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                userId,
                game.Id)).Id;
            otherRequestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                userId,
                game.Id)).Id;
        }

        await using (var serviceDb = _fixture.NewDbContext())
        {
            var result = await NewService(serviceDb).CancelAsync(cancelledRequestId, userId);
            Assert.Equal(ReviewRequestOutcome.Cancelled, result.Outcome);
        }

        await using var violationDb = _fixture.NewDbContext();
        var refundEntryId = await violationDb.ReviewRequests
            .Where(request => request.Id == cancelledRequestId)
            .Select(request => request.RefundEntryId)
            .SingleAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            violationDb.Database.ExecuteSqlRawAsync(
                "UPDATE review_requests SET refund_entry_id = @refundEntryId WHERE id = @requestId",
                [
                    new NpgsqlParameter("refundEntryId", refundEntryId!),
                    new NpgsqlParameter("requestId", otherRequestId),
                ],
                TestContext.Current.CancellationToken));

        Assert.Equal(UniqueViolation, exception.SqlState);
        Assert.Equal(RefundEntryIndex, exception.ConstraintName);
    }

    [Fact]
    public async Task CancelAsync_WhenAClaimCommitsFirst_WaitsAndThenDoesNotRefund()
    {
        long requesterId;
        long reviewerId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            requesterId = requester.Id;
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requesterId);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requesterId,
                game.Id)).Id;
        }

        var creditBefore = await BalanceAsync(requesterId);

        await using var claimDb = _fixture.NewDbContext();
        await claimDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await claimDb.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '2s'",
            TestContext.Current.CancellationToken);
        await using var claimTransaction = await claimDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var claimResult = await NewClaimService(claimDb).ClaimNextAsync(reviewerId);
        Assert.Equal(ReviewClaimOutcome.Claimed, claimResult.Outcome);
        Assert.Equal(requestId, claimResult.RequestId);

        await using var cancellationDb = _fixture.NewDbContext();
        await cancellationDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await cancellationDb.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '2s'",
            TestContext.Current.CancellationToken);
        var cancellationBackendId =
            ((NpgsqlConnection)cancellationDb.Database.GetDbConnection()).ProcessID;
        var cancellationTask = NewService(cancellationDb).CancelAsync(requestId, requesterId);

        await AssertBackendWaitsForLockAsync(cancellationBackendId, cancellationTask);

        await claimTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var cancellationResult = await cancellationTask;

        await using var verificationDb = _fixture.NewDbContext();
        var requestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var assignmentCount = await verificationDb.ReviewAssignments.CountAsync(
            assignment => assignment.RequestId == requestId,
            TestContext.Current.CancellationToken);
        var refundCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == requesterId && entry.Kind == CreditEntryKind.Refund,
            TestContext.Current.CancellationToken);
        var creditAfter = await verificationDb.Users
            .Where(user => user.Id == requesterId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRequestOutcome.NotCancellable, cancellationResult.Outcome);
        Assert.Equal(ReviewRequestStatus.Assigned, requestStatus);
        Assert.Equal(1, assignmentCount);
        Assert.Equal(0, refundCount);
        Assert.Equal(creditBefore, creditAfter);
    }

    [Fact]
    public async Task ClaimNextAsync_WhenACancellationHasTheLock_SkipsItWithoutAssigning()
    {
        long requesterId;
        long reviewerId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            requesterId = requester.Id;
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requesterId);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requesterId,
                game.Id)).Id;
        }

        var creditBefore = await BalanceAsync(requesterId);

        await using var cancellationDb = _fixture.NewDbContext();
        await cancellationDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await cancellationDb.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '2s'",
            TestContext.Current.CancellationToken);
        await using var cancellationTransaction =
            await cancellationDb.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var cancellationResult = await NewService(cancellationDb).CancelAsync(
            requestId,
            requesterId);
        Assert.Equal(ReviewRequestOutcome.Cancelled, cancellationResult.Outcome);

        ReviewClaimResult claimResult;
        await using (var claimDb = _fixture.NewDbContext())
        {
            await claimDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await claimDb.Database.ExecuteSqlRawAsync(
                "SET lock_timeout = '2s'",
                TestContext.Current.CancellationToken);
            claimResult = await NewClaimService(claimDb).ClaimNextAsync(reviewerId);
        }

        await cancellationTransaction.CommitAsync(TestContext.Current.CancellationToken);

        await using var verificationDb = _fixture.NewDbContext();
        var requestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var assignmentCount = await verificationDb.ReviewAssignments.CountAsync(
            assignment => assignment.RequestId == requestId,
            TestContext.Current.CancellationToken);
        var refundCount = await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == requesterId && entry.Kind == CreditEntryKind.Refund,
            TestContext.Current.CancellationToken);
        var creditAfter = await verificationDb.Users
            .Where(user => user.Id == requesterId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.NoneAvailable, claimResult.Outcome);
        Assert.Null(claimResult.RequestId);
        Assert.Equal(ReviewRequestStatus.Cancelled, requestStatus);
        Assert.Equal(0, assignmentCount);
        Assert.Equal(1, refundCount);
        Assert.Equal(creditBefore + 1, creditAfter);
    }

    private async Task AssertBackendWaitsForLockAsync(
        int backendId,
        Task<ReviewRequestResult> cancellationTask)
    {
        await using var observerDb = _fixture.NewDbContext();
        var waitDeadline = DateTimeOffset.UtcNow.AddMilliseconds(1500);

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

            if (cancellationTask.IsCompleted)
            {
                var prematureResult = await cancellationTask;
                Assert.Fail(
                    $"Cancellation completed before reaching the forced lock conflict: " +
                    $"outcome={prematureResult.Outcome}.");
            }

            Assert.True(
                DateTimeOffset.UtcNow < waitDeadline,
                "Cancellation did not reach a PostgreSQL lock wait within 1.5 seconds.");
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RequestAsync_ReusesTheSameOperationAndKeepsADifferentOperationDistinct()
    {
        long userId;
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            userId = user.Id;
            gameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        var operationId = Guid.NewGuid();
        ReviewRequestResult first;
        ReviewRequestResult replay;
        ReviewRequestResult distinct;
        await using (var firstDb = _fixture.NewDbContext())
        {
            first = await NewService(firstDb).RequestAsync(gameId, userId, operationId);
        }

        await using (var replayDb = _fixture.NewDbContext())
        {
            replay = await NewService(replayDb).RequestAsync(gameId, userId, operationId);
        }

        await using (var distinctDb = _fixture.NewDbContext())
        {
            distinct = await NewService(distinctDb)
                .RequestAsync(gameId, userId, Guid.NewGuid());
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(ReviewRequestOutcome.Queued, first.Outcome);
        Assert.Equal(first.RequestId, replay.RequestId);
        Assert.True(replay.IsReplay);
        Assert.Equal(ReviewRequestOutcome.Queued, distinct.Outcome);
        Assert.NotEqual(first.RequestId, distinct.RequestId);
        Assert.Equal(2, await verificationDb.ReviewRequests.CountAsync(
            request => request.RequesterId == userId,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == userId && entry.Kind == CreditEntryKind.Spend,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenOperationIsReusedForAnotherGame_ReturnsConflict()
    {
        long userId;
        long firstGameId;
        long secondGameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            userId = user.Id;
            firstGameId = (await CreateGameAsync(arrangeDb, userId)).Id;
            secondGameId = (await CreateGameAsync(arrangeDb, userId)).Id;
        }

        var operationId = Guid.NewGuid();
        await using (var firstDb = _fixture.NewDbContext())
        {
            Assert.Equal(
                ReviewRequestOutcome.Queued,
                (await NewService(firstDb)
                    .RequestAsync(firstGameId, userId, operationId)).Outcome);
        }

        await using var conflictDb = _fixture.NewDbContext();
        var conflict = await NewService(conflictDb)
            .RequestAsync(secondGameId, userId, operationId);

        Assert.Equal(ReviewRequestOutcome.OperationConflict, conflict.Outcome);
        Assert.Single(await conflictDb.ReviewRequests
            .Where(request => request.RequesterId == userId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenOperationIdIsEmpty_WritesNothing()
    {
        await using var arrangeDb = _fixture.NewDbContext();
        var user = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
        var game = await CreateGameAsync(arrangeDb, user.Id);

        var result = await NewService(arrangeDb)
            .RequestAsync(game.Id, user.Id, Guid.Empty);

        Assert.Equal(ReviewRequestOutcome.InvalidOperationId, result.Outcome);
        Assert.Empty(await arrangeDb.ReviewRequests
            .Where(request => request.RequesterId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    // The service only uses its logger for outcome records; these tests assert database state.
    private static ReviewRequestService NewService(AppDbContext db) =>
        new(db, NullLogger<ReviewRequestService>.Instance);

    private static ReviewClaimService NewClaimService(AppDbContext db) =>
        new(db, NullLogger<ReviewClaimService>.Instance);

    private async Task<int> BalanceAsync(long userId)
    {
        await using var db = _fixture.NewDbContext();
        return await db.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<User> CreateUserWithCreditAsync(AppDbContext db, int balance)
    {
        var user = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");

        if (balance > 0)
        {
            db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                User = user,
                Kind = CreditEntryKind.Grant,
                Delta = balance,
            });
        }

        // CreditPageTests sweeps every user and compares balance with ledger sum. Writing
        // only the ledger lets the database trigger derive valid test data in every order.
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Game> CreateGameAsync(AppDbContext db, long ownerId)
    {
        var game = new Game
        {
            OwnerId = ownerId,
            Title = $"Test Game {Guid.NewGuid():N}",
        };

        db.Games.Add(game);
        await db.SaveChangesAsync();
        return game;
    }

    private static async Task<int> LedgerSumAsync(AppDbContext db, long userId) =>
        await db.CreditLedgerEntries
            .Where(entry => entry.UserId == userId)
            .SumAsync(
                entry => (int?)entry.Delta,
                TestContext.Current.CancellationToken) ?? 0;
}
