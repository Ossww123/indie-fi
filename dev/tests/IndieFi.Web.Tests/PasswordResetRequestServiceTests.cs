using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordResetRequestServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public PasswordResetRequestServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RequestAsync_WhenEligible_ReplacesTokenAndMessageAtomically()
    {
        var email = $"reset-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(email);
        var oldToken = CreatePlaintextToken(51);
        await SeedPendingResetAsync(userId, oldToken, Now.AddMinutes(-2));

        PasswordResetRequestResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                IPAddress.Parse("203.0.113.50"),
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var tokens = await verificationDb.PasswordResetTokens
            .Where(token => token.UserId == userId)
            .OrderBy(token => token.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var message = await verificationDb.AuthenticationEmailOutboxMessages.SingleAsync(
            item => item.UserId == userId
                && item.Kind == AuthenticationEmailMessageKind.PasswordReset,
            TestContext.Current.CancellationToken);
        var attempt = await verificationDb.PasswordResetRequestAttempts.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        var newToken = Unprotect(message);

        Assert.Equal(PasswordResetRequestOutcome.RequestAccepted, result.Outcome);
        Assert.Null(result.EmailError);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(Now, tokens[0].RevokedAt);
        Assert.Null(tokens[1].RevokedAt);
        Assert.Equal(HashToken(oldToken), tokens[0].TokenHash);
        Assert.Equal(HashToken(newToken), tokens[1].TokenHash);
        Assert.Equal(Now, tokens[1].CreatedAt);
        Assert.Equal(Now.Add(PasswordResetTokenService.TokenLifetime), tokens[1].ExpiresAt);
        Assert.Equal(tokens[1].ExpiresAt, message.ExpiresAt);
        Assert.Equal(Now, attempt.CreatedAt);
        Assert.Equal(RequestIpProtectionService.HashSize, attempt.RequestIpHash.Length);
        Assert.DoesNotContain("203.0.113.50", Convert.ToHexString(attempt.RequestIpHash));
    }

    [Theory]
    [InlineData(AccountCase.Unknown, "203.0.113.51")]
    [InlineData(AccountCase.Unconfirmed, "203.0.113.52")]
    [InlineData(AccountCase.Passwordless, "203.0.113.53")]
    public async Task RequestAsync_WhenAccountIsIneligible_ReturnsCommonResultWithoutEmail(
        AccountCase accountCase,
        string requestIpText)
    {
        var email = $"ineligible-{accountCase}-{Guid.NewGuid():N}@example.test";
        long? userId = accountCase switch
        {
            AccountCase.Unknown => null,
            AccountCase.Unconfirmed => await CreateUserAsync(email, confirmed: false),
            AccountCase.Passwordless => await CreateUserAsync(
                email,
                passwordHash: null),
            _ => throw new ArgumentOutOfRangeException(nameof(accountCase)),
        };
        var requestIp = IPAddress.Parse(requestIpText);

        PasswordResetRequestResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                requestIp,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var ipHash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(requestIp);
        var attempt = await verificationDb.PasswordResetRequestAttempts.SingleAsync(
            item => item.RequestIpHashKeyId == ipHash.KeyId
                && item.RequestIpHash.SequenceEqual(ipHash.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRequestOutcome.RequestAccepted, result.Outcome);
        Assert.Null(result.EmailError);
        Assert.Null(attempt.UserId);
        Assert.False(await verificationDb.PasswordResetTokens.AnyAsync(
            token => userId != null && token.UserId == userId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            message => userId != null && message.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenEmailIsInvalid_ReturnsValidationErrorWithoutAttempt()
    {
        var beforeCount = await CountAttemptsAsync();

        PasswordResetRequestResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                "invalid-email",
                IPAddress.Parse("203.0.113.54"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(PasswordResetRequestOutcome.InvalidEmail, result.Outcome);
        Assert.Equal("사용할 수 없는 이메일 형식입니다.", result.EmailError);
        Assert.Equal(beforeCount, await CountAttemptsAsync());
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public async Task RequestAsync_EnforcesOneMinuteIntervalAtBoundary(
        int secondsSinceLastIssue,
        bool expectedEmail)
    {
        var email = $"minute-{secondsSinceLastIssue}-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(email);
        await SeedTokenAsync(userId, Now.AddSeconds(-secondsSinceLastIssue));

        PasswordResetRequestResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                IPAddress.Parse(
                    expectedEmail ? "203.0.113.55" : "203.0.113.56"),
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var tokens = await verificationDb.PasswordResetTokens
            .Where(token => token.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRequestOutcome.RequestAccepted, result.Outcome);
        Assert.Equal(expectedEmail ? 2 : 1, tokens.Count);
        Assert.Equal(
            expectedEmail,
            await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
                message => message.UserId == userId
                    && message.Kind == AuthenticationEmailMessageKind.PasswordReset,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            expectedEmail ? 1 : 0,
            await verificationDb.PasswordResetRequestAttempts.CountAsync(
                attempt => attempt.UserId == userId,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(3599, false)]
    [InlineData(3600, true)]
    public async Task RequestAsync_EnforcesFivePerAccountAtHourlyBoundary(
        int secondsSinceRequests,
        bool expectedEmail)
    {
        var email = $"account-hour-{secondsSinceRequests}-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(email);
        await SeedAttemptsAsync(
            userId,
            IPAddress.Parse("198.51.100.60"),
            PasswordResetRequestService.AccountHourlyLimit,
            Now.AddSeconds(-secondsSinceRequests));

        PasswordResetRequestResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                IPAddress.Parse(
                    expectedEmail ? "203.0.113.57" : "203.0.113.58"),
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(PasswordResetRequestOutcome.RequestAccepted, result.Outcome);
        Assert.Equal(
            expectedEmail,
            await verificationDb.PasswordResetTokens.AnyAsync(
                token => token.UserId == userId,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            expectedEmail ? 1 : PasswordResetRequestService.AccountHourlyLimit,
            await verificationDb.PasswordResetRequestAttempts.CountAsync(
                attempt => attempt.UserId == userId,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(3599, 20)]
    [InlineData(3600, 1)]
    public async Task RequestAsync_EnforcesTwentyPerIpWithTheSamePublicResult(
        int secondsSinceRequests,
        int expectedRemainingAttempts)
    {
        var requestIp = IPAddress.Parse(
            secondsSinceRequests == 3599
                ? "203.0.113.59"
                : "203.0.113.60");
        await SeedAttemptsAsync(
            userId: null,
            requestIp,
            PasswordResetRequestService.IpHourlyLimit,
            Now.AddSeconds(-secondsSinceRequests));

        PasswordResetRequestResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                $"unknown-{Guid.NewGuid():N}@example.test",
                requestIp,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var ipHash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(requestIp);
        var remaining = await verificationDb.PasswordResetRequestAttempts.CountAsync(
            attempt => attempt.RequestIpHashKeyId == ipHash.KeyId
                && attempt.RequestIpHash.SequenceEqual(ipHash.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal(PasswordResetRequestOutcome.RequestAccepted, result.Outcome);
        Assert.Equal(expectedRemainingAttempts, remaining);
    }

    [Fact]
    public async Task RequestAsync_WhenIpLimitIsReached_DoesNotQueueEligibleAccount()
    {
        var email = $"ip-limited-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(email);
        var requestIp = IPAddress.Parse("203.0.113.65");
        await SeedAttemptsAsync(
            userId: null,
            requestIp,
            PasswordResetRequestService.IpHourlyLimit,
            Now.AddMinutes(-1));

        PasswordResetRequestResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                requestIp,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(PasswordResetRequestOutcome.RequestAccepted, result.Outcome);
        Assert.False(await verificationDb.PasswordResetTokens.AnyAsync(
            token => token.UserId == userId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            message => message.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenSameAccountIsConcurrent_QueuesOneEmail()
    {
        var email = $"account-concurrent-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(email);

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        var firstTask = NewService(firstDb).RequestAsync(
            email,
            IPAddress.Parse("203.0.113.61"),
            TestContext.Current.CancellationToken);
        var secondTask = NewService(secondDb).RequestAsync(
            email,
            IPAddress.Parse("203.0.113.62"),
            TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(firstTask, secondTask);

        await using var verificationDb = _fixture.NewDbContext();
        var firstIpHash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(IPAddress.Parse("203.0.113.61"));
        var secondIpHash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(IPAddress.Parse("203.0.113.62"));
        Assert.All(
            results,
            result => Assert.Equal(
                PasswordResetRequestOutcome.RequestAccepted,
                result.Outcome));
        Assert.Single(await verificationDb.PasswordResetTokens
            .Where(token => token.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verificationDb.AuthenticationEmailOutboxMessages
            .Where(message => message.UserId == userId
                && message.Kind == AuthenticationEmailMessageKind.PasswordReset)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            2,
            await verificationDb.PasswordResetRequestAttempts.CountAsync(
                attempt => attempt.RequestIpHashKeyId == firstIpHash.KeyId
                    && (attempt.RequestIpHash.SequenceEqual(firstIpHash.Value)
                        || attempt.RequestIpHash.SequenceEqual(secondIpHash.Value)),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await verificationDb.PasswordResetRequestAttempts.CountAsync(
                attempt => attempt.UserId == userId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenSameIpIsConcurrent_AcceptsAllButStoresTwenty()
    {
        var requestIp = IPAddress.Parse("203.0.113.63");
        var tasks = Enumerable.Range(0, PasswordResetRequestService.IpHourlyLimit + 1)
            .Select(async index =>
            {
                await using var db = _fixture.NewDbContext();
                return await NewService(db).RequestAsync(
                    $"ip-concurrent-{index}-{Guid.NewGuid():N}@example.test",
                    requestIp,
                    TestContext.Current.CancellationToken);
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        await using var verificationDb = _fixture.NewDbContext();
        var ipHash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(requestIp);
        Assert.All(
            results,
            result => Assert.Equal(
                PasswordResetRequestOutcome.RequestAccepted,
                result.Outcome));
        Assert.Equal(
            PasswordResetRequestService.IpHourlyLimit,
            await verificationDb.PasswordResetRequestAttempts.CountAsync(
                attempt => attempt.RequestIpHashKeyId == ipHash.KeyId
                    && attempt.RequestIpHash.SequenceEqual(ipHash.Value),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenOutboxInsertFails_RollsBackAttemptAndToken()
    {
        var email = $"rollback-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(email);
        var oldToken = CreatePlaintextToken(52);
        await SeedPendingResetAsync(userId, oldToken, Now.AddMinutes(-2));

        await using (var db = _fixture.NewDbContext(new ThrowOnOutboxInsertInterceptor()))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => NewService(db).RequestAsync(
                    email,
                    IPAddress.Parse("203.0.113.64"),
                    TestContext.Current.CancellationToken));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.PasswordResetRequestAttempts.AnyAsync(
            attempt => attempt.UserId == userId,
            TestContext.Current.CancellationToken));
        var storedToken = await verificationDb.PasswordResetTokens.SingleAsync(
            token => token.UserId == userId,
            TestContext.Current.CancellationToken);
        var storedMessage = await verificationDb.AuthenticationEmailOutboxMessages.SingleAsync(
            message => message.UserId == userId
                && message.Kind == AuthenticationEmailMessageKind.PasswordReset,
            TestContext.Current.CancellationToken);

        Assert.Null(storedToken.RevokedAt);
        Assert.Equal(HashToken(oldToken), storedToken.TokenHash);
        Assert.Equal(oldToken, Unprotect(storedMessage));
    }

    private PasswordResetRequestService NewService(AppDbContext db) =>
        new(
            db,
            TestAccountProtection.CreateService(),
            TestAccountProtection.CreateRequestIpProtectionService(),
            new PasswordResetTokenService(db, new FixedTimeProvider(Now)),
            new AuthenticationEmailOutboxService(
                db,
                TestAccountProtection.CreateMessageProtectionService(),
                new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));

    private async Task<long> CreateUserAsync(
        string email,
        bool confirmed = true,
        string? passwordHash = "test-password-hash")
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email, passwordHash);
        user.EmailConfirmedAt = confirmed ? Now.AddDays(-1) : null;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task SeedPendingResetAsync(
        long userId,
        string plaintextToken,
        DateTimeOffset createdAt)
    {
        await using var db = _fixture.NewDbContext();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = HashToken(plaintextToken),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(PasswordResetTokenService.TokenLifetime),
        });
        new AuthenticationEmailOutboxService(
                db,
                TestAccountProtection.CreateMessageProtectionService(),
                new FixedTimeProvider(createdAt))
            .StagePasswordReset(
                userId,
                plaintextToken,
                createdAt.Add(PasswordResetTokenService.TokenLifetime));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedTokenAsync(long userId, DateTimeOffset createdAt)
    {
        await using var db = _fixture.NewDbContext();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = RandomNumberGenerator.GetBytes(SHA256.HashSizeInBytes),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(PasswordResetTokenService.TokenLifetime),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedAttemptsAsync(
        long? userId,
        IPAddress requestIp,
        int count,
        DateTimeOffset createdAt)
    {
        var hash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(requestIp);
        await using var db = _fixture.NewDbContext();
        db.PasswordResetRequestAttempts.AddRange(
            Enumerable.Range(0, count).Select(_ => new PasswordResetRequestAttempt
            {
                UserId = userId,
                RequestIpHash = hash.Value.ToArray(),
                RequestIpHashKeyId = hash.KeyId,
                CreatedAt = createdAt,
            }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAttemptsAsync()
    {
        await using var db = _fixture.NewDbContext();
        return await db.PasswordResetRequestAttempts.CountAsync(
            TestContext.Current.CancellationToken);
    }

    private static string CreatePlaintextToken(byte value) =>
        WebEncoders.Base64UrlEncode(
            Enumerable.Repeat(value, PasswordResetTokenService.TokenEntropySize)
                .ToArray());

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Unprotect(AuthenticationEmailOutboxMessage message) =>
        TestAccountProtection.CreateMessageProtectionService().Unprotect(
            message.UserId,
            message.Kind,
            new ProtectedAuthenticationEmailPayload(
                message.PayloadCiphertext,
                message.PayloadNonce,
                message.PayloadAuthenticationTag,
                message.PayloadKeyId));

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    public enum AccountCase
    {
        Unknown,
        Unconfirmed,
        Passwordless,
    }

    private sealed class ThrowOnOutboxInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "INSERT INTO authentication_email_outbox_messages",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Forced Outbox insert failure.");
            }

            return base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }
}
