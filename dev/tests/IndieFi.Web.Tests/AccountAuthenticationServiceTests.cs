using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountAuthenticationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AccountAuthenticationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthenticateAsync_WithCorrectPassword_ReturnsUserIdentity()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();
        var userId = await CreateUserAsync(email, password, emailConfirmed: true);

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher,
                    TestAccountProtection.CreateService(),
                    new FixedTimeProvider(Now))
                .AuthenticateAsync(
                    $"  {email.ToUpperInvariant()}  ",
                    password,
                    TestContext.Current.CancellationToken);
        }

        Assert.Equal(AccountAuthenticationOutcome.Authenticated, result.Outcome);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(1, result.SessionVersion);
    }

    [Fact]
    public async Task AuthenticateAsync_WithWrongPassword_ReturnsInvalidCredentialsWithoutIdentity()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();

        await CreateUserAsync(email, password, emailConfirmed: false);

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher,
                    TestAccountProtection.CreateService(),
                    new FixedTimeProvider(Now))
                .AuthenticateAsync(
                    email,
                    "Wrong-Test-Password-456!",
                    TestContext.Current.CancellationToken);
        }

        Assert.Equal(AccountAuthenticationOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.UserId);
        Assert.Null(result.SessionVersion);
    }

    [Fact]
    public async Task AuthenticateAsync_WithCorrectPasswordBeforeEmailConfirmation_RequiresConfirmation()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();
        await CreateUserAsync(email, password, emailConfirmed: false);

        await using var authenticationDb = _fixture.NewDbContext();
        var result = await new AccountAuthenticationService(
                authenticationDb,
                passwordHasher,
                TestAccountProtection.CreateService(),
                new FixedTimeProvider(Now))
            .AuthenticateAsync(
                email,
                password,
                TestContext.Current.CancellationToken);

        Assert.Equal(AccountAuthenticationOutcome.EmailNotConfirmed, result.Outcome);
        Assert.Null(result.UserId);
        Assert.Null(result.SessionVersion);
    }

    [Fact]
    public async Task AuthenticateAsync_WithUnknownEmail_ReturnsSameInvalidCredentialsResult()
    {
        var passwordHasher = new RecordingPasswordHasher();

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher,
                    TestAccountProtection.CreateService(),
                    new FixedTimeProvider(Now))
                .AuthenticateAsync(
                    $"{Guid.NewGuid():N}@example.test",
                    "Any-Test-Password-123!",
                    TestContext.Current.CancellationToken);
        }

        Assert.Equal(AccountAuthenticationOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.UserId);
        Assert.Null(result.SessionVersion);
        Assert.Equal(1, passwordHasher.VerifyCallCount);
        Assert.NotNull(passwordHasher.VerifiedHash);
    }

    [Theory]
    [InlineData(AccountStatus.TemporarilySuspended)]
    [InlineData(AccountStatus.IndefinitelySuspended)]
    public async Task AuthenticateAsync_WithSuspendedAccount_DoesNotReturnIdentity(
        AccountStatus accountStatus)
    {
        var email = $"suspended-login-{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var suspendedUntil = accountStatus == AccountStatus.TemporarilySuspended
            ? Now.AddHours(1)
            : (DateTimeOffset?)null;
        await CreateUserAsync(
            email,
            password,
            emailConfirmed: true,
            accountStatus,
            suspendedUntil,
            sessionVersion: 7);

        await using var authenticationDb = _fixture.NewDbContext();
        var result = await CreateService(authenticationDb).AuthenticateAsync(
            email,
            password,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountAuthenticationOutcome.AccountUnavailable, result.Outcome);
        Assert.Null(result.UserId);
        Assert.Null(result.SessionVersion);
    }

    [Fact]
    public async Task AuthenticateAsync_AfterTemporarySuspensionExpires_ReactivatesBeforeReturningIdentity()
    {
        var email = $"expired-suspension-login-{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var userId = await CreateUserAsync(
            email,
            password,
            emailConfirmed: true,
            AccountStatus.TemporarilySuspended,
            Now.AddMinutes(-1),
            sessionVersion: 7);

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await CreateService(authenticationDb).AuthenticateAsync(
                email,
                password,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(AccountAuthenticationOutcome.Authenticated, result.Outcome);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(8, result.SessionVersion);

        await using var assertionDb = _fixture.NewDbContext();
        var user = await assertionDb.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(AccountStatus.Active, user.AccountStatus);
        Assert.Null(user.SuspendedUntil);
        Assert.Equal(8, user.SessionVersion);
    }

    [Fact]
    public async Task AuthenticateAsync_WithWrongPassword_DoesNotReactivateExpiredSuspension()
    {
        var email = $"expired-suspension-wrong-{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var userId = await CreateUserAsync(
            email,
            password,
            emailConfirmed: true,
            AccountStatus.TemporarilySuspended,
            Now.AddMinutes(-1),
            sessionVersion: 7);

        await using (var authenticationDb = _fixture.NewDbContext())
        {
            var result = await CreateService(authenticationDb).AuthenticateAsync(
                email,
                "Wrong-Test-Password-456!",
                TestContext.Current.CancellationToken);
            Assert.Equal(AccountAuthenticationOutcome.InvalidCredentials, result.Outcome);
        }

        await using var assertionDb = _fixture.NewDbContext();
        var user = await assertionDb.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(AccountStatus.TemporarilySuspended, user.AccountStatus);
        Assert.Equal(Now.AddMinutes(-1), user.SuspendedUntil);
        Assert.Equal(7, user.SessionVersion);
    }

    private static AccountAuthenticationService CreateService(AppDbContext db) =>
        new(
            db,
            new PasswordHasher<User>(),
            TestAccountProtection.CreateService(),
            new FixedTimeProvider(Now));

    private async Task<long> CreateUserAsync(
        string email,
        string password,
        bool emailConfirmed,
        AccountStatus accountStatus = AccountStatus.Active,
        DateTimeOffset? suspendedUntil = null,
        long sessionVersion = 1)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email, passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        user.AccountStatus = accountStatus;
        user.SuspendedUntil = suspendedUntil;
        user.SessionVersion = sessionVersion;
        if (emailConfirmed)
        {
            user.EmailConfirmedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
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

    private sealed class RecordingPasswordHasher : IPasswordHasher<User>
    {
        public int VerifyCallCount { get; private set; }
        public string? VerifiedHash { get; private set; }

        public string HashPassword(User user, string password) =>
            throw new NotSupportedException();

        public PasswordVerificationResult VerifyHashedPassword(
            User user,
            string hashedPassword,
            string providedPassword)
        {
            VerifyCallCount++;
            VerifiedHash = hashedPassword;
            return PasswordVerificationResult.Failed;
        }
    }
}
