using System.Data.Common;
using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailConfirmationTokenConsumptionTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 8, 14, 4, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ConsumedAt = IssuedAt.AddHours(1);

    private readonly PostgreSqlFixture _fixture;

    public EmailConfirmationTokenConsumptionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConsumeAsync_WithValidToken_MarksTokenUsedAndConfirmsEmailTogether()
    {
        var issued = await CreateIssuedTokenAsync();

        EmailConfirmationTokenConsumeResult result;
        await using (var consumeDb = _fixture.NewDbContext())
        {
            result = await NewService(consumeDb, ConsumedAt).ConsumeAsync(
                issued.Token,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var token = await verificationDb.EmailConfirmationTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);
        var confirmedAt = await verificationDb.Users
            .Where(user => user.Id == issued.UserId)
            .Select(user => user.EmailConfirmedAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EmailConfirmationTokenConsumeOutcome.Confirmed, result.Outcome);
        Assert.Equal(ConsumedAt, token.UsedAt);
        Assert.Null(token.RevokedAt);
        Assert.Equal(ConsumedAt, confirmedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-confirmation-token")]
    public async Task ConsumeAsync_WithMalformedToken_ReturnsInvalidWithoutDatabaseCall(
        string? plaintextToken)
    {
        var counter = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(counter);

        var result = await NewService(db, ConsumedAt).ConsumeAsync(
            plaintextToken,
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailConfirmationTokenConsumeOutcome.Invalid, result.Outcome);
        Assert.Equal(0, counter.Count);
    }

    [Fact]
    public async Task ConsumeAsync_WithUnknownWellFormedToken_ReturnsInvalid()
    {
        var unknownToken = WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(EmailConfirmationTokenService.TokenEntropySize));

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db, ConsumedAt).ConsumeAsync(
            unknownToken,
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailConfirmationTokenConsumeOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task ConsumeAsync_AtExpiry_ReturnsInvalidWithoutChangingAccount()
    {
        var issued = await CreateIssuedTokenAsync();
        var expiresAt = IssuedAt.Add(EmailConfirmationTokenService.TokenLifetime);

        await using (var consumeDb = _fixture.NewDbContext())
        {
            var result = await NewService(consumeDb, expiresAt).ConsumeAsync(
                issued.Token,
                TestContext.Current.CancellationToken);
            Assert.Equal(EmailConfirmationTokenConsumeOutcome.Invalid, result.Outcome);
        }

        await AssertAccountRemainsUnconfirmedAsync(issued.UserId);
    }

    [Fact]
    public async Task ConsumeAsync_WithRevokedToken_ReturnsInvalidWithoutChangingAccount()
    {
        var issued = await CreateIssuedTokenAsync();

        await using (var reissueDb = _fixture.NewDbContext())
        {
            var reissued = await NewService(reissueDb, IssuedAt.AddMinutes(1)).IssueAsync(
                issued.UserId,
                TestContext.Current.CancellationToken);
            Assert.Equal(EmailConfirmationTokenIssueOutcome.Issued, reissued.Outcome);
        }

        await using (var consumeDb = _fixture.NewDbContext())
        {
            var result = await NewService(consumeDb, ConsumedAt).ConsumeAsync(
                issued.Token,
                TestContext.Current.CancellationToken);
            Assert.Equal(EmailConfirmationTokenConsumeOutcome.Invalid, result.Outcome);
        }

        await AssertAccountRemainsUnconfirmedAsync(issued.UserId);
    }

    [Fact]
    public async Task ConsumeAsync_WhenUsedAgain_ReturnsInvalidAndPreservesFirstConfirmationTime()
    {
        var issued = await CreateIssuedTokenAsync();

        await using (var firstDb = _fixture.NewDbContext())
        {
            var first = await NewService(firstDb, ConsumedAt).ConsumeAsync(
                issued.Token,
                TestContext.Current.CancellationToken);
            Assert.Equal(EmailConfirmationTokenConsumeOutcome.Confirmed, first.Outcome);
        }

        await using (var secondDb = _fixture.NewDbContext())
        {
            var second = await NewService(secondDb, ConsumedAt.AddMinutes(1)).ConsumeAsync(
                issued.Token,
                TestContext.Current.CancellationToken);
            Assert.Equal(EmailConfirmationTokenConsumeOutcome.Invalid, second.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var token = await verificationDb.EmailConfirmationTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);
        var confirmedAt = await verificationDb.Users
            .Where(user => user.Id == issued.UserId)
            .Select(user => user.EmailConfirmedAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConsumedAt, token.UsedAt);
        Assert.Equal(ConsumedAt, confirmedAt);
    }

    [Fact]
    public async Task ConsumeAsync_WhenSameTokenIsUsedConcurrently_ConfirmsExactlyOnce()
    {
        var issued = await CreateIssuedTokenAsync();

        await using var blockerDb = _fixture.NewDbContext();
        await blockerDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await blockerDb.Database
            .SqlQueryRaw<long>(
                "SELECT id AS \"Value\" FROM users WHERE id = @userId FOR UPDATE",
                new NpgsqlParameter("userId", issued.UserId))
            .SingleAsync(TestContext.Current.CancellationToken);

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        await PrepareForLockTestAsync(firstDb);
        await PrepareForLockTestAsync(secondDb);

        var firstBackendId = ((NpgsqlConnection)firstDb.Database.GetDbConnection()).ProcessID;
        var secondBackendId = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        var firstTask = NewService(firstDb, ConsumedAt).ConsumeAsync(
            issued.Token,
            TestContext.Current.CancellationToken);
        var secondTask = NewService(secondDb, ConsumedAt).ConsumeAsync(
            issued.Token,
            TestContext.Current.CancellationToken);

        await AssertBackendWaitsForLockAsync(firstBackendId, firstTask);
        await AssertBackendWaitsForLockAsync(secondBackendId, secondTask);
        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(
            results,
            result => result.Outcome == EmailConfirmationTokenConsumeOutcome.Confirmed);
        Assert.Single(
            results,
            result => result.Outcome == EmailConfirmationTokenConsumeOutcome.Invalid);

        await using var verificationDb = _fixture.NewDbContext();
        var token = await verificationDb.EmailConfirmationTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);
        var confirmedAt = await verificationDb.Users
            .Where(user => user.Id == issued.UserId)
            .Select(user => user.EmailConfirmedAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConsumedAt, token.UsedAt);
        Assert.Equal(ConsumedAt, confirmedAt);
    }

    [Fact]
    public async Task ConsumeAsync_WhenConfirmingAccountFails_RollsBackTokenUse()
    {
        var issued = await CreateIssuedTokenAsync();
        var interceptor = new ThrowOnAccountConfirmationInterceptor();

        await using (var consumeDb = _fixture.NewDbContext(interceptor))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewService(consumeDb, ConsumedAt).ConsumeAsync(
                    issued.Token,
                    TestContext.Current.CancellationToken));
        }

        await AssertAccountRemainsUnconfirmedAsync(issued.UserId);
    }

    private async Task<IssuedToken> CreateIssuedTokenAsync()
    {
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(
                arrangeDb,
                $"{Guid.NewGuid():N}@example.test");
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        await using var issueDb = _fixture.NewDbContext();
        var issue = await NewService(issueDb, IssuedAt).IssueAsync(
            userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(EmailConfirmationTokenIssueOutcome.Issued, issue.Outcome);
        return new IssuedToken(userId, issue.Token!);
    }

    private async Task AssertAccountRemainsUnconfirmedAsync(long userId)
    {
        await using var verificationDb = _fixture.NewDbContext();
        var token = await verificationDb.EmailConfirmationTokens
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
        var confirmedAt = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.EmailConfirmedAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Null(token.UsedAt);
        Assert.Null(confirmedAt);
    }

    private static EmailConfirmationTokenService NewService(
        AppDbContext db,
        DateTimeOffset now) =>
        new(db, new FixedTimeProvider(now));

    private static async Task PrepareForLockTestAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '5s'",
            TestContext.Current.CancellationToken);
    }

    private async Task AssertBackendWaitsForLockAsync(
        int backendId,
        Task<EmailConfirmationTokenConsumeResult> consumeTask)
    {
        await using var observerDb = _fixture.NewDbContext();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

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

            if (consumeTask.IsCompleted)
            {
                var result = await consumeTask;
                Assert.Fail(
                    $"Token consumption completed before reaching the forced lock conflict: " +
                    $"outcome={result.Outcome}.");
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "Token consumption did not reach a PostgreSQL lock wait within 2 seconds.");
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }
    }

    private sealed record IssuedToken(long UserId, string Token);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class ThrowOnAccountConfirmationInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "UPDATE users",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Forced account confirmation failure.");
            }

            return new ValueTask<InterceptionResult<int>>(result);
        }
    }
}
