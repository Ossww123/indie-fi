using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailConfirmationTokenServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 3, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public EmailConfirmationTokenServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IssueAsync_CreatesOneDayTokenAndStoresOnlyItsHash()
    {
        var userId = await CreateUserAsync();

        EmailConfirmationTokenIssueResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb).IssueAsync(
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.EmailConfirmationTokens.SingleAsync(
            token => token.UserId == userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(EmailConfirmationTokenIssueOutcome.Issued, result.Outcome);
        Assert.NotNull(result.Token);
        Assert.Equal(EmailConfirmationTokenService.TokenEntropySize, WebEncoders.Base64UrlDecode(result.Token).Length);
        Assert.Equal(Now, stored.CreatedAt);
        Assert.Equal(Now.Add(EmailConfirmationTokenService.TokenLifetime), stored.ExpiresAt);
        Assert.Equal(stored.ExpiresAt, result.ExpiresAt);
        Assert.Equal(SHA256.HashSizeInBytes, stored.TokenHash.Length);
        Assert.Equal(HashToken(result.Token), stored.TokenHash);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task IssueAsync_WhenIssuedAgain_RevokesPreviousUnresolvedToken()
    {
        var userId = await CreateUserAsync();
        EmailConfirmationTokenIssueResult first;
        EmailConfirmationTokenIssueResult second;

        await using (var firstDb = _fixture.NewDbContext())
        {
            first = await NewService(firstDb).IssueAsync(
                userId,
                TestContext.Current.CancellationToken);
        }

        await using (var secondDb = _fixture.NewDbContext())
        {
            second = await NewService(secondDb).IssueAsync(
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var tokens = await verificationDb.EmailConfirmationTokens
            .Where(token => token.UserId == userId)
            .OrderBy(token => token.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, tokens.Count);
        Assert.Equal(Now, tokens[0].RevokedAt);
        Assert.Null(tokens[1].RevokedAt);
        Assert.Equal(HashToken(first.Token!), tokens[0].TokenHash);
        Assert.Equal(HashToken(second.Token!), tokens[1].TokenHash);
    }

    [Fact]
    public async Task IssueAsync_WhenAccountDoesNotExist_ReturnsNotFoundWithoutToken()
    {
        EmailConfirmationTokenIssueResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).IssueAsync(
                long.MaxValue,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(EmailConfirmationTokenIssueOutcome.UserNotFound, result.Outcome);
        Assert.Null(result.Token);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task IssueAsync_WhenEmailIsAlreadyConfirmed_ReturnsAlreadyConfirmedWithoutToken()
    {
        var userId = await CreateUserAsync(emailConfirmedAt: Now.AddDays(-1));

        EmailConfirmationTokenIssueResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).IssueAsync(
                userId,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(EmailConfirmationTokenIssueOutcome.EmailAlreadyConfirmed, result.Outcome);
        Assert.Null(result.Token);
        Assert.Null(result.ExpiresAt);
        Assert.False(await verificationDb.EmailConfirmationTokens.AnyAsync(
            token => token.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IssueAsync_WhenSameAccountIsIssuedConcurrently_LeavesOneUnresolvedToken()
    {
        var userId = await CreateUserAsync();

        await using var blockerDb = _fixture.NewDbContext();
        await blockerDb.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await blockerDb.Database
            .SqlQueryRaw<long>(
                "SELECT id AS \"Value\" FROM users WHERE id = @userId FOR UPDATE",
                new NpgsqlParameter("userId", userId))
            .SingleAsync(TestContext.Current.CancellationToken);

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        await PrepareForLockTestAsync(firstDb);
        await PrepareForLockTestAsync(secondDb);

        var firstBackendId = ((NpgsqlConnection)firstDb.Database.GetDbConnection()).ProcessID;
        var secondBackendId = ((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        var firstTask = NewService(firstDb).IssueAsync(
            userId,
            TestContext.Current.CancellationToken);
        var secondTask = NewService(secondDb).IssueAsync(
            userId,
            TestContext.Current.CancellationToken);

        await AssertBackendWaitsForLockAsync(firstBackendId, firstTask);
        await AssertBackendWaitsForLockAsync(secondBackendId, secondTask);
        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(firstTask, secondTask);

        await using var verificationDb = _fixture.NewDbContext();
        var tokens = await verificationDb.EmailConfirmationTokens
            .Where(token => token.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(
            results,
            result => Assert.Equal(EmailConfirmationTokenIssueOutcome.Issued, result.Outcome));
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens, token => token.RevokedAt is null);
        Assert.Single(tokens, token => token.RevokedAt is not null);
        Assert.All(tokens, token => Assert.Null(token.UsedAt));

        var unresolved = tokens.Single(token => token.RevokedAt is null);
        Assert.Single(results, result => HashToken(result.Token!).SequenceEqual(unresolved.TokenHash));
    }

    [Fact]
    public async Task DatabaseRejectsTwoUnresolvedTokensForSameAccount()
    {
        var userId = await CreateUserAsync();

        await using var db = _fixture.NewDbContext();
        db.EmailConfirmationTokens.AddRange(
            NewStoredToken(userId, 31),
            NewStoredToken(userId, 32));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(
            "IX_email_confirmation_tokens_user_id_unresolved",
            postgresException.ConstraintName);
    }

    private static EmailConfirmationTokenService NewService(AppDbContext db) =>
        new(db, new FixedTimeProvider(Now));

    private async Task<long> CreateUserAsync(DateTimeOffset? emailConfirmedAt = null)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        user.EmailConfirmedAt = emailConfirmedAt;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private static EmailConfirmationToken NewStoredToken(long userId, byte value) =>
        new()
        {
            UserId = userId,
            TokenHash = Enumerable.Repeat(value, SHA256.HashSizeInBytes).ToArray(),
            CreatedAt = Now,
            ExpiresAt = Now.Add(EmailConfirmationTokenService.TokenLifetime),
        };

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static async Task PrepareForLockTestAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '5s'",
            TestContext.Current.CancellationToken);
    }

    private async Task AssertBackendWaitsForLockAsync(
        int backendId,
        Task<EmailConfirmationTokenIssueResult> issueTask)
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

            if (issueTask.IsCompleted)
            {
                var result = await issueTask;
                Assert.Fail(
                    $"Token issuance completed before reaching the forced lock conflict: " +
                    $"outcome={result.Outcome}.");
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "Token issuance did not reach a PostgreSQL lock wait within 2 seconds.");
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }
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
