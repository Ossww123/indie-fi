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
public sealed class EmailConfirmationResendServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public EmailConfirmationResendServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RequestAsync_WhenAllowed_ReplacesTokenAndPendingMessageAtomically()
    {
        var email = $"resend-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUnconfirmedUserAsync(email);
        var oldToken = CreatePlaintextToken(31);
        await SeedPendingConfirmationAsync(userId, oldToken, Now.AddMinutes(-2));

        EmailConfirmationResendResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                IPAddress.Parse("203.0.113.10"),
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var tokens = await verificationDb.EmailConfirmationTokens
            .Where(token => token.UserId == userId)
            .OrderBy(token => token.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var message = await verificationDb.AuthenticationEmailOutboxMessages.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        var attempt = await verificationDb.EmailConfirmationResendAttempts.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        var newToken = TestAccountProtection.CreateMessageProtectionService().Unprotect(
            userId,
            message.Kind,
            new ProtectedAuthenticationEmailPayload(
                message.PayloadCiphertext,
                message.PayloadNonce,
                message.PayloadAuthenticationTag,
                message.PayloadKeyId));

        Assert.Equal(EmailConfirmationResendOutcome.EmailQueued, result.Outcome);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(Now, tokens[0].RevokedAt);
        Assert.Null(tokens[1].RevokedAt);
        Assert.Equal(HashToken(oldToken), tokens[0].TokenHash);
        Assert.Equal(HashToken(newToken), tokens[1].TokenHash);
        Assert.Equal(Now, tokens[1].CreatedAt);
        Assert.Equal(tokens[1].ExpiresAt, message.ExpiresAt);
        Assert.Equal(Now, attempt.CreatedAt);
        Assert.Equal(RequestIpProtectionService.HashSize, attempt.RequestIpHash.Length);
        Assert.DoesNotContain("203.0.113.10", Convert.ToHexString(attempt.RequestIpHash));
    }

    [Theory]
    [InlineData(59, EmailConfirmationResendOutcome.AccountRateLimited)]
    [InlineData(60, EmailConfirmationResendOutcome.EmailQueued)]
    public async Task RequestAsync_EnforcesOneMinuteIntervalAtBoundary(
        int secondsSinceLastIssue,
        EmailConfirmationResendOutcome expected)
    {
        var email = $"minute-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUnconfirmedUserAsync(email);
        await SeedTokenAsync(userId, Now.AddSeconds(-secondsSinceLastIssue));

        EmailConfirmationResendResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                IPAddress.Parse("203.0.113.11"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(expected, result.Outcome);
    }

    [Theory]
    [InlineData(3599, EmailConfirmationResendOutcome.AccountRateLimited)]
    [InlineData(3600, EmailConfirmationResendOutcome.EmailQueued)]
    public async Task RequestAsync_EnforcesFivePerAccountAtHourlyBoundary(
        int secondsSinceRequests,
        EmailConfirmationResendOutcome expected)
    {
        var email = $"account-hour-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUnconfirmedUserAsync(email);
        await SeedAttemptsAsync(
            userId,
            IPAddress.Parse("203.0.113.12"),
            count: EmailConfirmationResendService.AccountHourlyLimit,
            Now.AddSeconds(-secondsSinceRequests));

        EmailConfirmationResendResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                IPAddress.Parse("203.0.113.13"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(expected, result.Outcome);
    }

    [Theory]
    [InlineData(3599, EmailConfirmationResendOutcome.IpRateLimited, 20)]
    [InlineData(3600, EmailConfirmationResendOutcome.RequestAccepted, 1)]
    public async Task RequestAsync_EnforcesTwentyPerIpAtHourlyBoundary(
        int secondsSinceRequests,
        EmailConfirmationResendOutcome expected,
        int expectedRemainingAttempts)
    {
        var requestIp = IPAddress.Parse(
            secondsSinceRequests == 3599
                ? "203.0.113.14"
                : "203.0.113.19");
        await SeedAttemptsAsync(
            userId: null,
            requestIp,
            count: EmailConfirmationResendService.IpHourlyLimit,
            Now.AddSeconds(-secondsSinceRequests));

        EmailConfirmationResendResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                $"unknown-{Guid.NewGuid():N}@example.test",
                requestIp,
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var ipHash = Assert.Single(
            TestAccountProtection.CreateRequestIpProtectionService()
                .CreateReadableHashes(requestIp));
        var remaining = await verificationDb.EmailConfirmationResendAttempts.CountAsync(
            attempt =>
                attempt.RequestIpHashKeyId == ipHash.KeyId
                && attempt.RequestIpHash.SequenceEqual(ipHash.Value),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expectedRemainingAttempts, remaining);
    }

    [Fact]
    public async Task RequestAsync_WhenSameAccountIsRequestedConcurrently_QueuesOneEmail()
    {
        var email = $"account-concurrent-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUnconfirmedUserAsync(email);

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        var firstTask = NewService(firstDb).RequestAsync(
            email,
            IPAddress.Parse("203.0.113.15"),
            TestContext.Current.CancellationToken);
        var secondTask = NewService(secondDb).RequestAsync(
            email,
            IPAddress.Parse("203.0.113.16"),
            TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(firstTask, secondTask);

        await using var verificationDb = _fixture.NewDbContext();
        var firstIpHash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(IPAddress.Parse("203.0.113.15"));
        var secondIpHash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(IPAddress.Parse("203.0.113.16"));
        Assert.Single(
            results,
            result => result.Outcome == EmailConfirmationResendOutcome.EmailQueued);
        Assert.Single(
            results,
            result => result.Outcome == EmailConfirmationResendOutcome.AccountRateLimited);
        Assert.Single(await verificationDb.EmailConfirmationTokens
            .Where(token =>
                token.UserId == userId
                && token.UsedAt == null
                && token.RevokedAt == null)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verificationDb.AuthenticationEmailOutboxMessages
            .Where(message => message.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            2,
            await verificationDb.EmailConfirmationResendAttempts.CountAsync(
                attempt =>
                    attempt.RequestIpHashKeyId == firstIpHash.KeyId
                    && (attempt.RequestIpHash.SequenceEqual(firstIpHash.Value)
                        || attempt.RequestIpHash.SequenceEqual(secondIpHash.Value)),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await verificationDb.EmailConfirmationResendAttempts.CountAsync(
                attempt => attempt.UserId == userId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenSameIpIsRequestedConcurrently_AcceptsExactlyTwenty()
    {
        var requestIp = IPAddress.Parse("203.0.113.17");
        var tasks = Enumerable.Range(0, EmailConfirmationResendService.IpHourlyLimit + 1)
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

        Assert.Equal(
            EmailConfirmationResendService.IpHourlyLimit,
            results.Count(result =>
                result.Outcome == EmailConfirmationResendOutcome.RequestAccepted));
        Assert.Single(
            results,
            result => result.Outcome == EmailConfirmationResendOutcome.IpRateLimited);
    }

    [Fact]
    public async Task RequestAsync_WhenEmailIsAlreadyConfirmed_AcceptsWithoutQueuingEmail()
    {
        var email = $"confirmed-{Guid.NewGuid():N}@example.test";
        long userId;
        await using (var setupDb = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(setupDb, email);
            user.EmailConfirmedAt = Now.AddMinutes(-1);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        EmailConfirmationResendResult result;
        await using (var db = _fixture.NewDbContext())
        {
            result = await NewService(db).RequestAsync(
                email,
                IPAddress.Parse("203.0.113.20"),
                TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(EmailConfirmationResendOutcome.RequestAccepted, result.Outcome);
        Assert.False(await verificationDb.EmailConfirmationTokens.AnyAsync(
            token => token.UserId == userId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            message => message.UserId == userId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.EmailConfirmationResendAttempts.AnyAsync(
            attempt => attempt.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestAsync_WhenOutboxInsertFails_RollsBackAttemptAndTokenReplacement()
    {
        var email = $"rollback-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUnconfirmedUserAsync(email);
        var oldToken = CreatePlaintextToken(32);
        await SeedPendingConfirmationAsync(userId, oldToken, Now.AddMinutes(-2));
        var interceptor = new ThrowOnOutboxInsertInterceptor();

        await using (var db = _fixture.NewDbContext(interceptor))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => NewService(db).RequestAsync(
                    email,
                    IPAddress.Parse("203.0.113.18"),
                    TestContext.Current.CancellationToken));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.EmailConfirmationResendAttempts.AnyAsync(
            attempt => attempt.UserId == userId,
            TestContext.Current.CancellationToken));
        var storedToken = await verificationDb.EmailConfirmationTokens.SingleAsync(
            token => token.UserId == userId,
            TestContext.Current.CancellationToken);
        var storedMessage = await verificationDb.AuthenticationEmailOutboxMessages.SingleAsync(
            message => message.UserId == userId,
            TestContext.Current.CancellationToken);
        var storedPlaintext = TestAccountProtection.CreateMessageProtectionService().Unprotect(
            userId,
            storedMessage.Kind,
            new ProtectedAuthenticationEmailPayload(
                storedMessage.PayloadCiphertext,
                storedMessage.PayloadNonce,
                storedMessage.PayloadAuthenticationTag,
                storedMessage.PayloadKeyId));

        Assert.Null(storedToken.RevokedAt);
        Assert.Equal(HashToken(oldToken), storedToken.TokenHash);
        Assert.Equal(oldToken, storedPlaintext);
    }

    private EmailConfirmationResendService NewService(AppDbContext db) =>
        new(
            db,
            TestAccountProtection.CreateService(),
            TestAccountProtection.CreateRequestIpProtectionService(),
            new EmailConfirmationTokenService(db, new FixedTimeProvider(Now)),
            new AuthenticationEmailOutboxService(
                db,
                TestAccountProtection.CreateMessageProtectionService(),
                new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));

    private async Task<long> CreateUnconfirmedUserAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task SeedPendingConfirmationAsync(
        long userId,
        string plaintextToken,
        DateTimeOffset createdAt)
    {
        await using var db = _fixture.NewDbContext();
        db.EmailConfirmationTokens.Add(new EmailConfirmationToken
        {
            UserId = userId,
            TokenHash = HashToken(plaintextToken),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(EmailConfirmationTokenService.TokenLifetime),
        });
        new AuthenticationEmailOutboxService(
                db,
                TestAccountProtection.CreateMessageProtectionService(),
                new FixedTimeProvider(createdAt))
            .StageEmailConfirmation(
                userId,
                plaintextToken,
                createdAt.Add(EmailConfirmationTokenService.TokenLifetime));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedTokenAsync(long userId, DateTimeOffset createdAt)
    {
        await using var db = _fixture.NewDbContext();
        db.EmailConfirmationTokens.Add(new EmailConfirmationToken
        {
            UserId = userId,
            TokenHash = RandomNumberGenerator.GetBytes(SHA256.HashSizeInBytes),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(EmailConfirmationTokenService.TokenLifetime),
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
        db.EmailConfirmationResendAttempts.AddRange(
            Enumerable.Range(0, count).Select(_ => new EmailConfirmationResendAttempt
            {
                UserId = userId,
                RequestIpHash = hash.Value.ToArray(),
                RequestIpHashKeyId = hash.KeyId,
                CreatedAt = createdAt,
            }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string CreatePlaintextToken(byte value) =>
        WebEncoders.Base64UrlEncode(
            Enumerable.Repeat(value, EmailConfirmationTokenService.TokenEntropySize)
                .ToArray());

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
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
