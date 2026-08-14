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
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordResetCompletionServiceTests
{
    private const string CurrentPassword = "Current-Test-Password-123!";
    private const string NewPassword = "New-Test-Password-456!";

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

    private static PasswordResetCompletionService NewService(AppDbContext db) =>
        new(db, new PasswordHasher<User>(), new FixedTimeProvider(Now));

    private async Task<(long UserId, string Token)> CreateIssuedTokenAsync(
        long sessionVersion)
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
            ExpiresAt = Now.AddMinutes(29),
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
}
