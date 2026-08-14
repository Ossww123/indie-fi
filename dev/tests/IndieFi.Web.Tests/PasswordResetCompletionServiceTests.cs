using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordResetCompletionServiceTests
{
    private const string CurrentPassword = "Current-Test-Password-123!";
    private const string NewPassword = "New-Test-Password-456!";
    private const string OtherNewPassword = "Other-New-Test-Password-789!";

    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public PasswordResetCompletionServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CompleteAsync_WithValidToken_ChangesPasswordConsumesTokenAndInvalidatesSessions()
    {
        var issued = await CreateIssuedTokenAsync(sessionVersion: 7);

        PasswordResetCompletionResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).CompleteAsync(
                issued.Token,
                NewPassword,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == issued.UserId,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.PasswordResetTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetCompletionOutcome.Completed, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(8, user.SessionVersion);
        AssertPasswordFails(user, CurrentPassword);
        AssertPasswordMatches(user, NewPassword);
        Assert.Equal(Now, token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task CompleteAsync_WhenPasswordSaveFails_RollsBackTokenAndSessionChanges()
    {
        var issued = await CreateIssuedTokenAsync(sessionVersion: 4);

        await using (var db = _fixture.NewDbContext(new ThrowOnSaveChangesInterceptor()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NewService(db).CompleteAsync(
                    issued.Token,
                    NewPassword,
                    TestContext.Current.CancellationToken));
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == issued.UserId,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.PasswordResetTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, user.SessionVersion);
        AssertPasswordMatches(user, CurrentPassword);
        AssertPasswordFails(user, NewPassword);
        Assert.Null(token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task CompleteAsync_WithInvalidNewPassword_DoesNotReadOrChangeToken(
        string? newPassword)
    {
        var roundTrips = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(roundTrips);

        var result = await NewService(db).CompleteAsync(
            CreatePlaintextToken(),
            newPassword,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetCompletionOutcome.InvalidNewPassword, result.Outcome);
        Assert.Equal(
            PasswordResetCompletionService.NewPasswordField,
            Assert.Single(result.Violations).Field);
        Assert.Equal(0, roundTrips.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-reset-token")]
    public async Task CompleteAsync_WithMalformedToken_DoesNotReadAnAccount(string? token)
    {
        var roundTrips = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(roundTrips);

        var result = await NewService(db).CompleteAsync(
            token,
            NewPassword,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetCompletionOutcome.InvalidToken, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(0, roundTrips.Count);
    }

    [Theory]
    [InlineData(UnusableTokenState.Unknown)]
    [InlineData(UnusableTokenState.Expired)]
    [InlineData(UnusableTokenState.Used)]
    [InlineData(UnusableTokenState.Revoked)]
    public async Task CompleteAsync_WithUnusableToken_ReturnsSameResultAndChangesNothing(
        UnusableTokenState state)
    {
        var usedAt = state == UnusableTokenState.Used
            ? Now.AddMinutes(-1)
            : (DateTimeOffset?)null;
        var revokedAt = state == UnusableTokenState.Revoked
            ? Now.AddMinutes(-1)
            : (DateTimeOffset?)null;
        var expiresAt = state == UnusableTokenState.Expired
            ? Now
            : Now.AddMinutes(29);
        var issued = await CreateIssuedTokenAsync(
            sessionVersion: 9,
            expiresAt,
            usedAt,
            revokedAt);
        var presentedToken = state == UnusableTokenState.Unknown
            ? CreatePlaintextToken()
            : issued.Token;

        PasswordResetCompletionResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).CompleteAsync(
                presentedToken,
                NewPassword,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == issued.UserId,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.PasswordResetTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetCompletionOutcome.InvalidToken, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(9, user.SessionVersion);
        AssertPasswordMatches(user, CurrentPassword);
        AssertPasswordFails(user, NewPassword);
        Assert.Equal(usedAt, token.UsedAt);
        Assert.Equal(revokedAt, token.RevokedAt);
    }

    [Fact]
    public async Task CompleteAsync_WhenSameTokenIsSubmittedConcurrently_CompletesOnlyOnce()
    {
        var issued = await CreateIssuedTokenAsync(sessionVersion: 12);

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
        var firstTask = NewService(firstDb).CompleteAsync(
            issued.Token,
            NewPassword,
            TestContext.Current.CancellationToken);
        var secondTask = NewService(secondDb).CompleteAsync(
            issued.Token,
            OtherNewPassword,
            TestContext.Current.CancellationToken);

        await AssertBackendWaitsForLockAsync(firstBackendId, firstTask);
        await AssertBackendWaitsForLockAsync(secondBackendId, secondTask);
        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.Single(
            results,
            result => result.Outcome == PasswordResetCompletionOutcome.Completed);
        Assert.Single(
            results,
            result => result.Outcome == PasswordResetCompletionOutcome.InvalidToken);

        var winningPassword = results[0].Outcome == PasswordResetCompletionOutcome.Completed
            ? NewPassword
            : OtherNewPassword;
        var losingPassword = results[0].Outcome == PasswordResetCompletionOutcome.Completed
            ? OtherNewPassword
            : NewPassword;

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == issued.UserId,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.PasswordResetTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(13, user.SessionVersion);
        AssertPasswordFails(user, CurrentPassword);
        AssertPasswordMatches(user, winningPassword);
        AssertPasswordFails(user, losingPassword);
        Assert.Equal(Now, token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    private static PasswordResetCompletionService NewService(AppDbContext db) =>
        new(db, new PasswordHasher<User>(), new FixedTimeProvider(Now));

    private async Task<(long UserId, string Token)> CreateIssuedTokenAsync(
        long sessionVersion,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? usedAt = null,
        DateTimeOffset? revokedAt = null)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"reset-completion-{Guid.NewGuid():N}@example.test",
            passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(
            user,
            CurrentPassword);
        user.EmailConfirmedAt = Now.AddDays(-1);
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var token = CreatePlaintextToken();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(token),
            CreatedAt = Now.AddMinutes(-1),
            ExpiresAt = expiresAt ?? Now.AddMinutes(29),
            UsedAt = usedAt,
            RevokedAt = revokedAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (user.Id, token);
    }

    private static string CreatePlaintextToken() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(
            PasswordResetTokenService.TokenEntropySize));

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static void AssertPasswordMatches(User user, string password)
    {
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(
                user,
                user.PasswordHash!,
                password));
    }

    private static void AssertPasswordFails(User user, string password)
    {
        Assert.Equal(
            PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(
                user,
                user.PasswordHash!,
                password));
    }

    private static async Task PrepareForLockTestAsync(AppDbContext db)
    {
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SET lock_timeout = '5s'",
            TestContext.Current.CancellationToken);
    }

    private async Task AssertBackendWaitsForLockAsync(
        int backendId,
        Task<PasswordResetCompletionResult> completionTask)
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

            if (completionTask.IsCompleted)
            {
                var result = await completionTask;
                Assert.Fail(
                    "Password reset completed before reaching the forced lock conflict: "
                    + $"outcome={result.Outcome}.");
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "Password reset did not reach a PostgreSQL lock wait within 2 seconds.");
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

    private sealed class ThrowOnSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Injected password save failure.");
        }
    }

    public enum UnusableTokenState
    {
        Unknown,
        Expired,
        Used,
        Revoked,
    }
}
