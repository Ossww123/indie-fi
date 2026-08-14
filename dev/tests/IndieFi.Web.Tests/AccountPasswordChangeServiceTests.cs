using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountPasswordChangeServiceTests
{
    private const string CurrentPassword = "Current-Test-Password-123!";
    private const string NewPassword = "New-Test-Password-456!";

    private readonly PostgreSqlFixture _fixture;

    public AccountPasswordChangeServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ChangeAsync_WithCorrectCurrentPassword_ReplacesHashAndSessionVersionTogether()
    {
        var userId = await CreateUserAsync(sessionVersion: 7);

        AccountPasswordChangeResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).ChangeAsync(
                userId,
                CurrentPassword,
                NewPassword,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountPasswordChangeOutcome.Changed, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(8, user.SessionVersion);
        AssertPasswordFails(user, CurrentPassword);
        AssertPasswordMatches(user, NewPassword);
    }

    [Fact]
    public async Task ChangeAsync_WithWrongCurrentPassword_LeavesAccountUnchanged()
    {
        var userId = await CreateUserAsync(sessionVersion: 3);
        string originalHash;
        await using (var db = _fixture.NewDbContext())
        {
            originalHash = (await db.Users.SingleAsync(
                account => account.Id == userId,
                TestContext.Current.CancellationToken)).PasswordHash!;
        }

        AccountPasswordChangeResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).ChangeAsync(
                userId,
                "Wrong-Test-Password-789!",
                NewPassword,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountPasswordChangeOutcome.InvalidCurrentPassword, result.Outcome);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(AccountPasswordChangeService.CurrentPasswordField, violation.Field);
        Assert.Equal(originalHash, user.PasswordHash);
        Assert.Equal(3, user.SessionVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task ChangeAsync_WithInvalidNewPassword_DoesNotReadOrWriteAccount(
        string? newPassword)
    {
        var roundTrips = new RoundTripCountingInterceptor();
        await using var db = _fixture.NewDbContext(roundTrips);

        var result = await NewService(db).ChangeAsync(
            long.MaxValue,
            CurrentPassword,
            newPassword,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountPasswordChangeOutcome.InvalidNewPassword, result.Outcome);
        Assert.Equal(
            AccountPasswordChangeService.NewPasswordField,
            Assert.Single(result.Violations).Field);
        Assert.Equal(0, roundTrips.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeAsync_WithUnavailableAccount_ReturnsUnavailable(
        bool createPasswordlessAccount)
    {
        var userId = createPasswordlessAccount
            ? await CreateUserAsync(passwordless: true)
            : long.MaxValue;

        await using var db = _fixture.NewDbContext();
        var result = await NewService(db).ChangeAsync(
            userId,
            CurrentPassword,
            NewPassword,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountPasswordChangeOutcome.AccountUnavailable, result.Outcome);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task ChangeAsync_WhenSameOldPasswordIsUsedConcurrently_AllowsOneChange()
    {
        const string otherNewPassword = "Other-New-Test-Password-789!";
        var userId = await CreateUserAsync(sessionVersion: 11);

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
        var firstTask = NewService(firstDb).ChangeAsync(
            userId,
            CurrentPassword,
            NewPassword,
            TestContext.Current.CancellationToken);
        var secondTask = NewService(secondDb).ChangeAsync(
            userId,
            CurrentPassword,
            otherNewPassword,
            TestContext.Current.CancellationToken);

        await AssertBackendWaitsForLockAsync(firstBackendId, firstTask);
        await AssertBackendWaitsForLockAsync(secondBackendId, secondTask);
        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.Single(
            results,
            result => result.Outcome == AccountPasswordChangeOutcome.Changed);
        Assert.Single(
            results,
            result => result.Outcome == AccountPasswordChangeOutcome.InvalidCurrentPassword);

        var winningPassword = results[0].Outcome == AccountPasswordChangeOutcome.Changed
            ? NewPassword
            : otherNewPassword;
        var losingPassword = results[0].Outcome == AccountPasswordChangeOutcome.Changed
            ? otherNewPassword
            : NewPassword;

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(12, user.SessionVersion);
        AssertPasswordFails(user, CurrentPassword);
        AssertPasswordMatches(user, winningPassword);
        AssertPasswordFails(user, losingPassword);
    }

    private static AccountPasswordChangeService NewService(AppDbContext db) =>
        new(db, new PasswordHasher<User>());

    private async Task<long> CreateUserAsync(
        long sessionVersion = 1,
        bool passwordless = false)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"password-change-{Guid.NewGuid():N}@example.test",
            passwordHash: null);
        user.PasswordHash = passwordless
            ? null
            : new PasswordHasher<User>().HashPassword(user, CurrentPassword);
        user.EmailConfirmedAt = DateTimeOffset.UtcNow;
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

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
        Task<AccountPasswordChangeResult> changeTask)
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

            if (changeTask.IsCompleted)
            {
                var result = await changeTask;
                Assert.Fail(
                    "Password change completed before reaching the forced lock conflict: " +
                    $"outcome={result.Outcome}.");
            }

            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "Password change did not reach a PostgreSQL lock wait within 2 seconds.");
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                TestContext.Current.CancellationToken);
        }
    }
}
