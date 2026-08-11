using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewClaimServiceTests
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    private readonly PostgreSqlFixture _fixture;

    public ReviewClaimServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimNextAsync_WhenNothingIsQueued_ReturnsNoneAvailable()
    {
        long reviewerId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
        }

        ReviewClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).ClaimNextAsync(reviewerId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var requestCount = await verificationDb.ReviewRequests.CountAsync(
            TestContext.Current.CancellationToken);
        var assignmentCount = await verificationDb.ReviewAssignments.CountAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.NoneAvailable, result.Outcome);
        Assert.Null(result.RequestId);
        Assert.Equal(0, requestCount);
        Assert.Equal(0, assignmentCount);
    }

    [Fact]
    public async Task ClaimNextAsync_TakesTheOldestQueuedRequest()
    {
        long reviewerId;
        var requestIds = new List<long>();

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 3);
            var game = await CreateGameAsync(arrangeDb, requester.Id);

            for (var index = 0; index < 3; index++)
            {
                requestIds.Add((await CreateQueuedRequestAsync(
                    arrangeDb,
                    requester.Id,
                    game.Id)).Id);
            }
        }

        ReviewClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).ClaimNextAsync(reviewerId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var assignedRequestId = await verificationDb.ReviewRequests
            .Where(request => request.Status == ReviewRequestStatus.Assigned)
            .Select(request => request.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
        Assert.Equal(requestIds[0], result.RequestId);
        Assert.Equal(requestIds[0], assignedRequestId);
    }

    [Fact]
    public async Task ClaimNextAsync_NeverHandsOutTheReviewersOwnRequest()
    {
        long reviewerId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var reviewer = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            reviewerId = reviewer.Id;
            var game = await CreateGameAsync(arrangeDb, reviewerId);
            requestId = (await CreateQueuedRequestAsync(arrangeDb, reviewerId, game.Id)).Id;
        }

        ReviewClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).ClaimNextAsync(reviewerId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var requestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var assignmentCount = await verificationDb.ReviewAssignments.CountAsync(
            assignment => assignment.RequestId == requestId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.NoneAvailable, result.Outcome);
        Assert.Null(result.RequestId);
        Assert.Equal(ReviewRequestStatus.Queued, requestStatus);
        Assert.Equal(0, assignmentCount);
    }

    [Fact]
    public async Task ClaimNextAsync_MarksTheRequestAssignedAndLeasesIt()
    {
        long reviewerId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            var game = await CreateGameAsync(arrangeDb, requester.Id);
            requestId = (await CreateQueuedRequestAsync(arrangeDb, requester.Id, game.Id)).Id;
        }

        var claimStartedAt = DateTimeOffset.UtcNow;
        ReviewClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).ClaimNextAsync(reviewerId);
        }

        var claimCompletedAt = DateTimeOffset.UtcNow;

        await using var verificationDb = _fixture.NewDbContext();
        var requestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var assignments = await verificationDb.ReviewAssignments
            .Where(assignment => assignment.RequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
        Assert.Equal(requestId, result.RequestId);
        Assert.Equal(ReviewRequestStatus.Assigned, requestStatus);
        var assignment = Assert.Single(assignments);
        Assert.Equal(ReviewAssignmentStatus.Active, assignment.Status);
        Assert.Equal(reviewerId, assignment.ReviewerId);
        Assert.InRange(
            assignment.LeasedUntil,
            claimStartedAt.AddMinutes(29),
            claimCompletedAt.AddMinutes(31));
    }

    [Fact]
    public async Task ClaimNextAsync_WhenManyReviewersRaceForOneRequest_ExactlyOneWins()
    {
        const int ReviewerCount = 8;
        long requestId;
        var reviewerIds = new List<long>();

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            var game = await CreateGameAsync(arrangeDb, requester.Id);
            requestId = (await CreateQueuedRequestAsync(arrangeDb, requester.Id, game.Id)).Id;

            for (var index = 0; index < ReviewerCount; index++)
            {
                reviewerIds.Add((await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id);
            }
        }

        // SKIP LOCKED makes contenders bypass the row held by the winner instead of waiting,
        // so the other seven find no candidate and return NoneAvailable. Without the lock, all
        // eight could select the same row and either violate the partial unique index or hand
        // one request to multiple reviewers.
        var results = await Task.WhenAll(reviewerIds.Select(async reviewerId =>
        {
            await using var serviceDb = _fixture.NewDbContext();
            return (
                ReviewerId: reviewerId,
                Result: await NewService(serviceDb).ClaimNextAsync(reviewerId));
        }));

        await using var verificationDb = _fixture.NewDbContext();
        var requestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == requestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var assignments = await verificationDb.ReviewAssignments
            .Where(assignment => assignment.RequestId == requestId)
            .ToListAsync(TestContext.Current.CancellationToken);

        var winner = Assert.Single(
            results,
            result => result.Result.Outcome == ReviewClaimOutcome.Claimed);
        Assert.Equal(
            ReviewerCount - 1,
            results.Count(result => result.Result.Outcome == ReviewClaimOutcome.NoneAvailable));
        Assert.Equal(requestId, winner.Result.RequestId);
        Assert.Equal(ReviewRequestStatus.Assigned, requestStatus);
        var assignment = Assert.Single(assignments);
        Assert.Equal(winner.ReviewerId, assignment.ReviewerId);
    }

    [Fact]
    public async Task ClaimNextAsync_WhenTheSameReviewerClaimsThatGameConcurrently_ReportsAlreadyHasThatGame()
    {
        long reviewerId;
        long firstRequestId;
        long secondRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requester.Id);
            firstRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
            secondRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
        }

        // Keeping A's transaction open makes its assignment invisible to B's snapshot while
        // preserving the reviewer-game conflict at the unique-index backstop.
        await using var firstServiceDb = _fixture.NewDbContext();
        await firstServiceDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var firstTransaction = await firstServiceDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var firstResult = await NewService(firstServiceDb).ClaimNextAsync(reviewerId);

        await using var secondServiceDb = _fixture.NewDbContext();
        await secondServiceDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        // A broken concurrency path must fail this test instead of waiting forever on A.
        await secondServiceDb.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '5s'",
            TestContext.Current.CancellationToken);

        var secondBackendId = ((NpgsqlConnection)secondServiceDb.Database.GetDbConnection()).ProcessID;
        var secondClaimTask = NewService(secondServiceDb).ClaimNextAsync(reviewerId);

        // Observing B's lock wait prevents A from committing before B has taken the conflicting
        // snapshot, which removes scheduler timing from the race.
        await using (var observerDb = _fixture.NewDbContext())
        {
            var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(6);

            while (true)
            {
                var secondClaimIsWaiting = await observerDb.Database
                    .SqlQueryRaw<bool>(
                        """
                        SELECT EXISTS (
                            SELECT 1
                              FROM pg_stat_activity
                             WHERE pid = @backendId
                               AND wait_event_type = 'Lock'
                        ) AS "Value"
                        """,
                        new NpgsqlParameter("backendId", secondBackendId))
                    .SingleAsync(TestContext.Current.CancellationToken);

                if (secondClaimIsWaiting)
                {
                    break;
                }

                if (secondClaimTask.IsCompleted)
                {
                    var prematureResult = await secondClaimTask;
                    Assert.Fail(
                        $"The second claim completed before reaching the forced conflict: " +
                        $"{prematureResult.Outcome}.");
                }

                Assert.True(
                    DateTimeOffset.UtcNow < waitDeadline,
                    "The second claim did not reach a PostgreSQL lock wait within six seconds.");
                await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    TestContext.Current.CancellationToken);
            }
        }

        await firstTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var secondResult = await secondClaimTask;

        await using var verificationDb = _fixture.NewDbContext();
        var assignmentCount = await verificationDb.ReviewAssignments.CountAsync(
            assignment => assignment.ReviewerId == reviewerId,
            TestContext.Current.CancellationToken);
        var secondRequestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == secondRequestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.Claimed, firstResult.Outcome);
        Assert.Equal(firstRequestId, firstResult.RequestId);
        Assert.Equal(ReviewClaimOutcome.AlreadyHasThatGame, secondResult.Outcome);
        Assert.Null(secondResult.RequestId);
        Assert.Equal(1, assignmentCount);
        Assert.Equal(ReviewRequestStatus.Queued, secondRequestStatus);
    }

    [Fact]
    public async Task ClaimNextAsync_WhenAnotherUniqueRuleIsViolated_LetsTheErrorThrough()
    {
        long firstReviewerId;
        long secondReviewerId;
        long firstRequestId;
        long secondRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            firstReviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            secondReviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var firstGame = await CreateGameAsync(arrangeDb, requester.Id);
            var secondGame = await CreateGameAsync(arrangeDb, requester.Id);
            firstRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                firstGame.Id)).Id;
            secondRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                secondGame.Id)).Id;
        }

        Assert.True(firstRequestId < secondRequestId);

        await using (var firstClaimDb = _fixture.NewDbContext())
        {
            var result = await NewService(firstClaimDb).ClaimNextAsync(firstReviewerId);
            Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
            Assert.Equal(firstRequestId, result.RequestId);
        }

        await using (var resetDb = _fixture.NewDbContext())
        {
            // Requeuing only the request preserves the live assignment and creates the invalid
            // state that an incorrect expiration flow could leave behind.
            var request = await resetDb.ReviewRequests.SingleAsync(
                item => item.Id == firstRequestId,
                TestContext.Current.CancellationToken);
            request.Status = ReviewRequestStatus.Queued;
            await resetDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var secondClaimDb = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await NewService(secondClaimDb).ClaimNextAsync(secondReviewerId));

        Assert.Equal(UniqueViolation, exception.SqlState);
        Assert.Equal("IX_review_assignments_request_id", exception.ConstraintName);
    }

    [Fact]
    public async Task ClaimNextAsync_WhenTheOldestRequestIsHeldByAnotherClaim_SkipsToTheNextOne()
    {
        long reviewerId;
        long firstRequestId;
        long secondRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            var game = await CreateGameAsync(arrangeDb, requester.Id);
            firstRequestId = (await CreateQueuedRequestAsync(arrangeDb, requester.Id, game.Id)).Id;
            secondRequestId = (await CreateQueuedRequestAsync(arrangeDb, requester.Id, game.Id)).Id;
        }

        // Firing concurrent claims does not reliably make them overlap: each one finishes in
        // about a millisecond, so the next claim's snapshot usually starts after the previous
        // one committed and no contention happens at all. Holding the head row open here forces
        // the collision that SKIP LOCKED exists to handle.
        await using var holdingDb = _fixture.NewDbContext();
        await holdingDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var holdingTransaction = await holdingDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await holdingDb.Database.ExecuteSqlRawAsync(
            "SELECT id FROM review_requests WHERE id = {0} FOR UPDATE",
            [firstRequestId],
            TestContext.Current.CancellationToken);

        ReviewClaimResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            await serviceDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

            // Without SKIP LOCKED the claim would wait for the transaction above instead of
            // failing, which would hang the whole test run rather than report a red test.
            await serviceDb.Database.ExecuteSqlRawAsync(
                "SET lock_timeout = '2s'",
                TestContext.Current.CancellationToken);

            result = await NewService(serviceDb).ClaimNextAsync(reviewerId);
        }

        await holdingTransaction.RollbackAsync(TestContext.Current.CancellationToken);

        // Skipping the locked head is what trades strict oldest-first ordering for throughput:
        // the second request is handed out while the first is still contended.
        Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
        Assert.Equal(secondRequestId, result.RequestId);
    }

    [Fact]
    public async Task ClaimNextAsync_WhenTheReviewerAlreadyHasThatGame_SkipsToADifferentGame()
    {
        long reviewerId;
        long firstGameRequestId;
        long secondGameRequestId;
        long skippedRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 3);
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var firstGame = await CreateGameAsync(arrangeDb, requester.Id);
            var secondGame = await CreateGameAsync(arrangeDb, requester.Id);
            firstGameRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                firstGame.Id)).Id;
            skippedRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                firstGame.Id)).Id;
            secondGameRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                secondGame.Id)).Id;
        }

        ReviewClaimResult firstResult;
        await using (var firstServiceDb = _fixture.NewDbContext())
        {
            firstResult = await NewService(firstServiceDb).ClaimNextAsync(reviewerId);
        }

        ReviewClaimResult secondResult;
        await using (var secondServiceDb = _fixture.NewDbContext())
        {
            secondResult = await NewService(secondServiceDb).ClaimNextAsync(reviewerId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var assignedGameIds = await verificationDb.ReviewAssignments
            .Where(assignment => assignment.ReviewerId == reviewerId)
            .Select(assignment => assignment.GameId)
            .ToListAsync(TestContext.Current.CancellationToken);
        var skippedRequestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == skippedRequestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.Claimed, firstResult.Outcome);
        Assert.Equal(firstGameRequestId, firstResult.RequestId);
        Assert.Equal(ReviewClaimOutcome.Claimed, secondResult.Outcome);
        Assert.Equal(secondGameRequestId, secondResult.RequestId);
        Assert.Equal(2, assignedGameIds.Count);
        Assert.Equal(2, assignedGameIds.Distinct().Count());
        Assert.Equal(ReviewRequestStatus.Queued, skippedRequestStatus);
    }

    [Fact]
    public async Task ClaimNextAsync_WhenEveryQueuedRequestIsForAGameTheReviewerAlreadyHas_ReturnsNoneAvailable()
    {
        long reviewerId;
        long firstRequestId;
        long secondRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requester.Id);
            firstRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
            secondRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
        }

        ReviewClaimResult firstResult;
        await using (var firstServiceDb = _fixture.NewDbContext())
        {
            firstResult = await NewService(firstServiceDb).ClaimNextAsync(reviewerId);
        }

        ReviewClaimResult secondResult;
        await using (var secondServiceDb = _fixture.NewDbContext())
        {
            secondResult = await NewService(secondServiceDb).ClaimNextAsync(reviewerId);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var secondRequestStatus = await verificationDb.ReviewRequests
            .Where(request => request.Id == secondRequestId)
            .Select(request => request.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var assignmentCount = await verificationDb.ReviewAssignments.CountAsync(
            assignment => assignment.ReviewerId == reviewerId,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReviewClaimOutcome.Claimed, firstResult.Outcome);
        Assert.Equal(firstRequestId, firstResult.RequestId);
        Assert.Equal(ReviewClaimOutcome.NoneAvailable, secondResult.Outcome);
        Assert.Null(secondResult.RequestId);
        Assert.Equal(ReviewRequestStatus.Queued, secondRequestStatus);
        Assert.Equal(1, assignmentCount);
    }

    [Fact]
    public async Task ClaimNextAsync_AfterTheReviewersAssignmentExpired_HandsOutThatGameAgain()
    {
        long reviewerId;
        long firstRequestId;
        long secondRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requester.Id);
            firstRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
            secondRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
        }

        ReviewClaimResult firstResult;
        await using (var firstServiceDb = _fixture.NewDbContext())
        {
            firstResult = await NewService(firstServiceDb).ClaimNextAsync(reviewerId);
        }

        await using (var expirationDb = _fixture.NewDbContext())
        {
            var assignment = await expirationDb.ReviewAssignments.SingleAsync(
                item => item.ReviewerId == reviewerId,
                TestContext.Current.CancellationToken);
            assignment.Status = ReviewAssignmentStatus.Expired;
            await expirationDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ReviewClaimResult secondResult;
        await using (var secondServiceDb = _fixture.NewDbContext())
        {
            secondResult = await NewService(secondServiceDb).ClaimNextAsync(reviewerId);
        }

        Assert.Equal(ReviewClaimOutcome.Claimed, firstResult.Outcome);
        Assert.Equal(firstRequestId, firstResult.RequestId);
        Assert.Equal(ReviewClaimOutcome.Claimed, secondResult.Outcome);
        Assert.Equal(secondRequestId, secondResult.RequestId);
    }

    [Fact]
    public async Task AssigningOneReviewerTheSameGameTwice_IsRefusedByTheDatabase()
    {
        long reviewerId;
        long gameId;
        long firstRequestId;
        long secondRequestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 2);
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var game = await CreateGameAsync(arrangeDb, requester.Id);
            gameId = game.Id;
            firstRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                gameId)).Id;
            secondRequestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                gameId)).Id;
        }

        await using (var serviceDb = _fixture.NewDbContext())
        {
            var result = await NewService(serviceDb).ClaimNextAsync(reviewerId);
            Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
            Assert.Equal(firstRequestId, result.RequestId);
        }

        await using (var attemptDb = _fixture.NewDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await attemptDb.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_assignments
                        (request_id, game_id, reviewer_id, status, leased_until)
                    VALUES ({0}, {1}, {2}, 'Active', now() + INTERVAL '30 minutes')
                    """,
                    new object[] { secondRequestId, gameId, reviewerId },
                    TestContext.Current.CancellationToken));

            Assert.Equal(UniqueViolation, exception.SqlState);
        }
    }

    [Fact]
    public async Task AssigningAGameThatDoesNotMatchItsRequest_IsRefusedByTheDatabase()
    {
        long reviewerId;
        long firstGameId;
        long secondGameId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await CreateUserWithCreditAsync(arrangeDb, balance: 1);
            reviewerId = (await CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var firstGame = await CreateGameAsync(arrangeDb, requester.Id);
            var secondGame = await CreateGameAsync(arrangeDb, requester.Id);
            firstGameId = firstGame.Id;
            secondGameId = secondGame.Id;
            requestId = (await CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                firstGameId)).Id;
        }

        await using (var attemptDb = _fixture.NewDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await attemptDb.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO review_assignments
                        (request_id, game_id, reviewer_id, status, leased_until)
                    VALUES ({0}, {1}, {2}, 'Active', now() + INTERVAL '30 minutes')
                    """,
                    new object[] { requestId, secondGameId, reviewerId },
                    TestContext.Current.CancellationToken));

            Assert.Equal(ForeignKeyViolation, exception.SqlState);
        }
    }

    // The service only uses its logger for outcome records; these tests assert database state.
    private static ReviewClaimService NewService(AppDbContext db) =>
        new(db, NullLogger<ReviewClaimService>.Instance);

    private static async Task<User> CreateUserWithCreditAsync(AppDbContext db, int balance)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "test-password-hash",
        };

        db.Users.Add(user);

        if (balance > 0)
        {
            db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                User = user,
                Kind = CreditEntryKind.Grant,
                Delta = balance,
            });
        }

        // Credit balance is derived by a database trigger, so ledger-only setup preserves the
        // invariant for tests that reconcile every user's cached balance with ledger history.
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return game;
    }

    private static async Task<ReviewRequest> CreateQueuedRequestAsync(
        AppDbContext db,
        long requesterId,
        long gameId)
    {
        var spendEntry = new CreditLedgerEntry
        {
            UserId = requesterId,
            Kind = CreditEntryKind.Spend,
            Delta = -1,
        };
        var request = new ReviewRequest
        {
            GameId = gameId,
            RequesterId = requesterId,
            SpendEntry = spendEntry,
        };

        db.CreditLedgerEntries.Add(spendEntry);
        db.ReviewRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request;
    }
}
