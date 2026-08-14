using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationEmailOutboxServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 5, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AuthenticationEmailOutboxServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StageEmailConfirmation_StoresOnlyEncryptedTokenPayload()
    {
        var userId = await CreateUserAsync();
        var plaintextToken = CreateToken();
        var expiresAt = Now.AddHours(24);

        await using (var stageDb = _fixture.NewDbContext())
        {
            NewService(stageDb).StageEmailConfirmation(
                userId,
                plaintextToken,
                expiresAt);
            await stageDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.AuthenticationEmailOutboxMessages
            .SingleAsync(
                message => message.UserId == userId,
                TestContext.Current.CancellationToken);
        var protectedPayload = new ProtectedAuthenticationEmailPayload(
            stored.PayloadCiphertext,
            stored.PayloadNonce,
            stored.PayloadAuthenticationTag,
            stored.PayloadKeyId);

        Assert.Equal(AuthenticationEmailMessageKind.EmailConfirmation, stored.Kind);
        Assert.Equal(Now, stored.CreatedAt);
        Assert.Equal(expiresAt, stored.ExpiresAt);
        Assert.Equal("message-test-v1", stored.PayloadKeyId);
        Assert.DoesNotContain(
            plaintextToken,
            Convert.ToBase64String(stored.PayloadCiphertext));
        Assert.Equal(
            plaintextToken,
            TestAccountProtection.CreateMessageProtectionService().Unprotect(
                userId,
                stored.Kind,
                protectedPayload));
    }

    [Fact]
    public async Task OutboxTable_HasNoPlaintextTokenColumn()
    {
        await using var db = _fixture.NewDbContext();
        var columns = await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT column_name AS "Value"
                  FROM information_schema.columns
                 WHERE table_schema = 'public'
                   AND table_name = 'authentication_email_outbox_messages'
                """)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains("payload_ciphertext", columns);
        Assert.Contains("payload_nonce", columns);
        Assert.Contains("payload_authentication_tag", columns);
        Assert.DoesNotContain(columns, column => column.Contains("token", StringComparison.Ordinal));
        Assert.DoesNotContain("payload_plaintext", columns);
    }

    [Fact]
    public async Task StageEmailConfirmation_DoesNotCommitCallerTransaction()
    {
        var userId = await CreateUserAsync();

        await using var stageDb = _fixture.NewDbContext();
        NewService(stageDb).StageEmailConfirmation(
            userId,
            CreateToken(),
            Now.AddHours(24));

        await using var observerDb = _fixture.NewDbContext();
        Assert.False(await observerDb.AuthenticationEmailOutboxMessages.AnyAsync(
            message => message.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerRollback_RemovesOutboxAndAccountChangeTogether()
    {
        var userId = await CreateUserAsync();

        await using (var transactionDb = _fixture.NewDbContext())
        {
            await using var transaction = await transactionDb.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            await transactionDb.Users
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(
                    updates => updates.SetProperty(user => user.EmailConfirmedAt, Now),
                    TestContext.Current.CancellationToken);
            NewService(transactionDb).StageEmailConfirmation(
                userId,
                CreateToken(),
                Now.AddHours(24));
            await transactionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var confirmedAt = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.EmailConfirmedAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Null(confirmedAt);
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            message => message.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseRejectsTwoPendingMessagesOfSameKindForAccount()
    {
        var userId = await CreateUserAsync();

        await using var db = _fixture.NewDbContext();
        NewService(db).StageEmailConfirmation(
            userId,
            CreateToken(),
            Now.AddHours(24));
        NewService(db).StageEmailConfirmation(
            userId,
            CreateToken(),
            Now.AddHours(24));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal(
            "IX_authentication_email_outbox_messages_user_id_kind",
            postgresException.ConstraintName);
    }

    [Fact]
    public async Task DatabaseRejectsInvalidProtectedPayloadShape()
    {
        var userId = await CreateUserAsync();

        await using var db = _fixture.NewDbContext();
        var message = NewService(db).StageEmailConfirmation(
            userId,
            CreateToken(),
            Now.AddHours(24));
        message.PayloadNonce = [1, 2, 3];

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(
            "CK_authentication_email_outbox_messages_payload_shape",
            postgresException.ConstraintName);
    }

    private AuthenticationEmailOutboxService NewService(AppDbContext db)
    {
        return new AuthenticationEmailOutboxService(
            db,
            TestAccountProtection.CreateMessageProtectionService(),
            new FixedTimeProvider(Now));
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

    private static string CreateToken()
    {
        return WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(EmailConfirmationTokenService.TokenEntropySize));
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
