using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordResetDataConstraintTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public PasswordResetDataConstraintTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DatabaseRejectsTwoUnresolvedTokensForSameAccount()
    {
        var userId = await CreateUserAsync();

        await using var db = _fixture.NewDbContext();
        db.PasswordResetTokens.AddRange(
            NewStoredToken(userId, 41),
            NewStoredToken(userId, 42));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(
            "IX_password_reset_tokens_user_id_unresolved",
            postgresException.ConstraintName);
    }

    [Fact]
    public async Task DatabaseAllowsResolvedAndOneUnresolvedTokenForSameAccount()
    {
        var userId = await CreateUserAsync();

        await using var db = _fixture.NewDbContext();
        var resolved = NewStoredToken(userId, 43);
        resolved.RevokedAt = Now;
        db.PasswordResetTokens.AddRange(resolved, NewStoredToken(userId, 44));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            2,
            await db.PasswordResetTokens.CountAsync(
                token => token.UserId == userId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseAllowsRequestAttemptWithoutKnownAccount()
    {
        await using var db = _fixture.NewDbContext();
        db.PasswordResetRequestAttempts.Add(new PasswordResetRequestAttempt
        {
            RequestIpHash = Enumerable.Repeat((byte)45, SHA256.HashSizeInBytes).ToArray(),
            RequestIpHashKeyId = "request-ip-test-v1",
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await db.PasswordResetRequestAttempts.SingleAsync(
            attempt => attempt.RequestIpHashKeyId == "request-ip-test-v1"
                && attempt.CreatedAt == Now,
            TestContext.Current.CancellationToken);
        Assert.Null(stored.UserId);
    }

    [Fact]
    public async Task DatabaseRejectsRequestAttemptWithInvalidIpHashLength()
    {
        await using var db = _fixture.NewDbContext();
        db.PasswordResetRequestAttempts.Add(new PasswordResetRequestAttempt
        {
            RequestIpHash = [1, 2, 3],
            RequestIpHashKeyId = "request-ip-test-v1",
            CreatedAt = Now.AddSeconds(1),
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "CK_password_reset_request_attempts_ip_hash_length",
            postgresException.ConstraintName);
    }

    [Fact]
    public async Task DatabaseAllowsPasswordResetAndConfirmationMessagesForSameAccount()
    {
        var userId = await CreateUserAsync();

        await using var db = _fixture.NewDbContext();
        db.AuthenticationEmailOutboxMessages.AddRange(
            NewOutboxMessage(userId, AuthenticationEmailMessageKind.EmailConfirmation),
            NewOutboxMessage(userId, AuthenticationEmailMessageKind.PasswordReset));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            2,
            await db.AuthenticationEmailOutboxMessages.CountAsync(
                message => message.UserId == userId,
                TestContext.Current.CancellationToken));
    }

    private async Task<long> CreateUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private static PasswordResetToken NewStoredToken(long userId, byte value) =>
        new()
        {
            UserId = userId,
            TokenHash = Enumerable.Repeat(value, SHA256.HashSizeInBytes).ToArray(),
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(30),
        };

    private static AuthenticationEmailOutboxMessage NewOutboxMessage(
        long userId,
        AuthenticationEmailMessageKind kind) =>
        new()
        {
            UserId = userId,
            Kind = kind,
            PayloadCiphertext = [1],
            PayloadNonce = Enumerable.Repeat((byte)2, 12).ToArray(),
            PayloadAuthenticationTag = Enumerable.Repeat((byte)3, 16).ToArray(),
            PayloadKeyId = "message-test-v1",
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(30),
            NextAttemptAt = Now,
        };
}
