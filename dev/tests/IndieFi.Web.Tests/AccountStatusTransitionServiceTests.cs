using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountStatusTransitionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AccountStatusTransitionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SuspendTemporarilyAsync_ChangesShapeAndInvalidatesSessions()
    {
        var userId = await CreateUserAsync(sessionVersion: 7);
        var suspendedUntil = Now.AddDays(3);

        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db).SuspendTemporarilyAsync(
            userId,
            suspendedUntil,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountStatusTransitionOutcome.Changed, result.Outcome);
        Assert.Equal(AccountStatus.TemporarilySuspended, result.Status);
        Assert.Equal(suspendedUntil, result.SuspendedUntil);
        Assert.Equal(8, result.SessionVersion);
        await AssertStoredStateAsync(
            userId,
            AccountStatus.TemporarilySuspended,
            suspendedUntil,
            sessionVersion: 8);
    }

    [Fact]
    public async Task SuspendIndefinitelyAsync_ClearsEndAndInvalidatesSessions()
    {
        var userId = await CreateUserAsync(
            AccountStatus.TemporarilySuspended,
            Now.AddDays(1),
            sessionVersion: 3);

        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db).SuspendIndefinitelyAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountStatusTransitionOutcome.Changed, result.Outcome);
        await AssertStoredStateAsync(
            userId,
            AccountStatus.IndefinitelySuspended,
            suspendedUntil: null,
            sessionVersion: 4);
    }

    [Fact]
    public async Task ReactivateAsync_ClearsSuspensionAndInvalidatesSessions()
    {
        var userId = await CreateUserAsync(
            AccountStatus.IndefinitelySuspended,
            suspendedUntil: null,
            sessionVersion: 11);

        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db).ReactivateAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountStatusTransitionOutcome.Changed, result.Outcome);
        await AssertStoredStateAsync(
            userId,
            AccountStatus.Active,
            suspendedUntil: null,
            sessionVersion: 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SuspendTemporarilyAsync_WithNonFutureEnd_DoesNotChangeAccount(
        int endOffsetTicks)
    {
        var userId = await CreateUserAsync(sessionVersion: 5);

        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db).SuspendTemporarilyAsync(
            userId,
            Now.AddTicks(endOffsetTicks),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountStatusTransitionOutcome.InvalidTemporarySuspensionEnd,
            result.Outcome);
        await AssertStoredStateAsync(
            userId,
            AccountStatus.Active,
            suspendedUntil: null,
            sessionVersion: 5);
    }

    [Fact]
    public async Task SameTransitionTwice_IsIdempotentAndInvalidatesSessionsOnce()
    {
        var userId = await CreateUserAsync(sessionVersion: 17);

        await using var firstDb = _fixture.NewDbContext();
        var first = await CreateService(firstDb).SuspendIndefinitelyAsync(
            userId,
            TestContext.Current.CancellationToken);
        await using var secondDb = _fixture.NewDbContext();
        var second = await CreateService(secondDb).SuspendIndefinitelyAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountStatusTransitionOutcome.Changed, first.Outcome);
        Assert.Equal(AccountStatusTransitionOutcome.Unchanged, second.Outcome);
        await AssertStoredStateAsync(
            userId,
            AccountStatus.IndefinitelySuspended,
            suspendedUntil: null,
            sessionVersion: 18);
    }

    [Fact]
    public async Task ReactivateExpiredTemporaryAsync_OnlyChangesExpiredSuspension()
    {
        var expiredUserId = await CreateUserAsync(
            AccountStatus.TemporarilySuspended,
            Now,
            sessionVersion: 20);
        var currentUserId = await CreateUserAsync(
            AccountStatus.TemporarilySuspended,
            Now.AddMilliseconds(1),
            sessionVersion: 30);

        await using var expiredDb = _fixture.NewDbContext();
        var expired = await CreateService(expiredDb).ReactivateExpiredTemporaryAsync(
            expiredUserId,
            TestContext.Current.CancellationToken);
        await using var currentDb = _fixture.NewDbContext();
        var current = await CreateService(currentDb).ReactivateExpiredTemporaryAsync(
            currentUserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountStatusTransitionOutcome.Changed, expired.Outcome);
        Assert.Equal(AccountStatusTransitionOutcome.NotEligible, current.Outcome);
        await AssertStoredStateAsync(
            expiredUserId,
            AccountStatus.Active,
            suspendedUntil: null,
            sessionVersion: 21);
        await AssertStoredStateAsync(
            currentUserId,
            AccountStatus.TemporarilySuspended,
            Now.AddMilliseconds(1),
            sessionVersion: 30);
    }

    [Fact]
    public async Task ConcurrentSameTransitions_ConvergeAndInvalidateSessionsOnce()
    {
        var userId = await CreateUserAsync(sessionVersion: 41);
        var suspendedUntil = Now.AddHours(2);
        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();

        var results = await Task.WhenAll(
            CreateService(firstDb).SuspendTemporarilyAsync(
                userId,
                suspendedUntil,
                TestContext.Current.CancellationToken),
            CreateService(secondDb).SuspendTemporarilyAsync(
                userId,
                suspendedUntil,
                TestContext.Current.CancellationToken));

        Assert.Single(
            results,
            result => result.Outcome == AccountStatusTransitionOutcome.Changed);
        Assert.Single(
            results,
            result => result.Outcome == AccountStatusTransitionOutcome.Unchanged);
        await AssertStoredStateAsync(
            userId,
            AccountStatus.TemporarilySuspended,
            suspendedUntil,
            sessionVersion: 42);
    }

    [Fact]
    public async Task TransitionAsync_WithUnknownUser_ReturnsNotFound()
    {
        await using var db = _fixture.NewDbContext();
        var result = await CreateService(db).ReactivateAsync(
            long.MaxValue,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountStatusTransitionOutcome.UserNotFound, result.Outcome);
        Assert.Null(result.Status);
        Assert.Null(result.SessionVersion);
    }

    private AccountStatusTransitionService CreateService(AppDbContext db)
    {
        return new AccountStatusTransitionService(db, new FixedTimeProvider(Now));
    }

    private async Task<long> CreateUserAsync(
        AccountStatus status = AccountStatus.Active,
        DateTimeOffset? suspendedUntil = null,
        long sessionVersion = 1)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"account-status-{Guid.NewGuid():N}@example.test");
        user.EmailConfirmedAt = Now.AddDays(-1);
        user.AccountStatus = status;
        user.SuspendedUntil = suspendedUntil;
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task AssertStoredStateAsync(
        long userId,
        AccountStatus status,
        DateTimeOffset? suspendedUntil,
        long sessionVersion)
    {
        await using var db = _fixture.NewDbContext();
        var stored = await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                user.AccountStatus,
                user.SuspendedUntil,
                user.SessionVersion,
            })
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(status, stored.AccountStatus);
        Assert.Equal(suspendedUntil, stored.SuspendedUntil);
        Assert.Equal(sessionVersion, stored.SessionVersion);
    }

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
