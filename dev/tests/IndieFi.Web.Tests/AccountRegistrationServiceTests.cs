using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountRegistrationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AccountRegistrationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RegisterAsync_CreatesHashedAccountAndStartingCreditTogether()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();
        var emailProtection = TestAccountProtection.CreateService();

        AccountRegistrationResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb, passwordHasher)
                .RegisterAsync(email, password, TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            item => item.Id == result.UserId,
            TestContext.Current.CancellationToken);
        var entry = await verificationDb.CreditLedgerEntries.SingleAsync(
            item => item.UserId == user.Id,
            TestContext.Current.CancellationToken);
        var lookup = await verificationDb.UserEmailLookups.SingleAsync(
            item => item.UserId == user.Id,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.EmailConfirmationTokens.SingleAsync(
            item => item.UserId == user.Id,
            TestContext.Current.CancellationToken);
        var outbox = await verificationDb.AuthenticationEmailOutboxMessages.SingleAsync(
            item => item.UserId == user.Id,
            TestContext.Current.CancellationToken);
        var plaintextToken = TestAccountProtection.CreateMessageProtectionService().Unprotect(
            user.Id,
            outbox.Kind,
            new ProtectedAuthenticationEmailPayload(
                outbox.PayloadCiphertext,
                outbox.PayloadNonce,
                outbox.PayloadAuthenticationTag,
                outbox.PayloadKeyId));

        Assert.Equal(AccountRegistrationOutcome.Registered, result.Outcome);
        Assert.Equal(user.Id, result.UserId);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash!, password));
        Assert.Equal(3, user.CreditBalance);
        Assert.Equal(CreditEntryKind.Grant, entry.Kind);
        Assert.Equal(3, entry.Delta);
        Assert.NotNull(user.EmailCiphertext);
        Assert.Equal(EmailProtectionService.NonceSize, user.EmailNonce?.Length);
        Assert.Equal(
            EmailProtectionService.AuthenticationTagSize,
            user.EmailAuthenticationTag?.Length);
        Assert.Equal("email-test-v1", user.EmailEncryptionKeyId);
        Assert.Equal(
            email,
            emailProtection.Unprotect(new ProtectedEmailAddress(
                user.EmailCiphertext!,
                user.EmailNonce!,
                user.EmailAuthenticationTag!,
                user.EmailEncryptionKeyId!)));
        Assert.Equal("lookup-test-v1", lookup.KeyId);
        Assert.Equal(EmailProtectionService.LookupValueSize, lookup.Value.Length);
        Assert.Null(user.EmailConfirmedAt);
        Assert.Equal(Now, token.CreatedAt);
        Assert.Equal(Now.Add(EmailConfirmationTokenService.TokenLifetime), token.ExpiresAt);
        Assert.Equal(token.ExpiresAt, outbox.ExpiresAt);
        Assert.Equal(
            token.TokenHash,
            SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken)));
    }

    [Fact]
    public async Task RegisterAsync_WhenEmailAndPasswordAreBothInvalid_ReportsBothAndWritesNothing()
    {
        AccountRegistrationResult result;
        long userCountBefore;
        long userCountAfter;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            userCountBefore = await serviceDb.Users.LongCountAsync(
                TestContext.Current.CancellationToken);
            result = await NewService(serviceDb, new PasswordHasher<User>())
                .RegisterAsync(
                    "not-an-email",
                    "short",
                    TestContext.Current.CancellationToken);

            userCountAfter = await serviceDb.Users.LongCountAsync(
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(AccountRegistrationOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [AccountRegistrationService.EmailField, AccountRegistrationService.PasswordField],
            result.Violations.Select(violation => violation.Field));
        Assert.Null(result.UserId);
        Assert.Equal(userCountBefore, userCountAfter);
    }

    [Fact]
    public async Task RegisterAsync_TrimsEmailAndStoresCaseInsensitiveLookupValue()
    {
        var localPart = Guid.NewGuid().ToString("N");
        var input = $"  {localPart}@Example.Test  ";
        var displayEmail = $"{localPart}@Example.Test";

        AccountRegistrationResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await NewService(serviceDb, new PasswordHasher<User>())
                .RegisterAsync(
                    input,
                    "Known-Test-Password-123!",
                    TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            item => item.Id == result.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountRegistrationOutcome.Registered, result.Outcome);
        Assert.Equal(
            displayEmail,
            TestAccountProtection.CreateService().Unprotect(
                new ProtectedEmailAddress(
                    user.EmailCiphertext,
                    user.EmailNonce,
                    user.EmailAuthenticationTag,
                    user.EmailEncryptionKeyId)));

        var expectedLookup = Assert.Single(
            TestAccountProtection.CreateService().CreateWritableLookupValues(
                new AccountEmailAddress(
                    displayEmail,
                    displayEmail.ToUpperInvariant())));
        var actualLookup = await verificationDb.UserEmailLookups.SingleAsync(
            item => item.UserId == user.Id,
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedLookup.Value, actualLookup.Value);
    }

    [Fact]
    public async Task RegisterAsync_WithDomainThatHasNoDot_ReturnsInvalidAndWritesNothing()
    {
        var email = $"{Guid.NewGuid():N}@invalid";

        await using var db = _fixture.NewDbContext();
        var userCountBefore = await db.Users.LongCountAsync(
            TestContext.Current.CancellationToken);
        var result = await NewService(db, new PasswordHasher<User>())
            .RegisterAsync(
                email,
                "Known-Test-Password-123!",
                TestContext.Current.CancellationToken);

        Assert.Equal(AccountRegistrationOutcome.Invalid, result.Outcome);
        Assert.Contains(
            result.Violations,
            violation => violation.Field == AccountRegistrationService.EmailField);
        Assert.Equal(
            userCountBefore,
            await db.Users.LongCountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisterAsync_WhenEmailAlreadyExists_ReturnsDuplicateAndWritesNothing()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string firstPassword = "First-Known-Password-123!";
        const string secondPassword = "Second-Known-Password-456!";
        var passwordHasher = new PasswordHasher<User>();
        long firstUserId;

        await using (var firstDb = _fixture.NewDbContext())
        {
            var firstResult = await NewService(firstDb, passwordHasher)
                .RegisterAsync(
                    email,
                    firstPassword,
                    TestContext.Current.CancellationToken);
            Assert.Equal(AccountRegistrationOutcome.Registered, firstResult.Outcome);
            firstUserId = firstResult.UserId!.Value;
        }

        AccountRegistrationResult duplicateResult;
        await using (var duplicateDb = _fixture.NewDbContext())
        {
            duplicateResult = await NewService(duplicateDb, passwordHasher)
                .RegisterAsync(
                    email.ToUpperInvariant(),
                    secondPassword,
                    TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            item => item.Id == firstUserId,
            TestContext.Current.CancellationToken);
        var entries = await verificationDb.CreditLedgerEntries
            .Where(entry => entry.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountRegistrationOutcome.EmailAlreadyRegistered,
            duplicateResult.Outcome);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash!, firstPassword));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash!, secondPassword));
        var entry = Assert.Single(entries);
        Assert.Equal(3, entry.Delta);
        Assert.Equal(3, user.CreditBalance);
        Assert.Single(await verificationDb.EmailConfirmationTokens
            .Where(token => token.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verificationDb.AuthenticationEmailOutboxMessages
            .Where(message => message.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisterAsync_WhenSameEmailArrivesAtOnce_CreatesOneAccountAndOneGrant()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var passwordHasher = new PasswordHasher<User>();

        var results = await Task.WhenAll(
            RegisterAsync(email, "First-Known-Password-123!"),
            RegisterAsync(email.ToUpperInvariant(), "Second-Known-Password-456!"));

        await using var verificationDb = _fixture.NewDbContext();
        var registered = Assert.Single(
            results,
            result => result.Outcome == AccountRegistrationOutcome.Registered);
        var user = await verificationDb.Users.SingleAsync(
            item => item.Id == registered.UserId,
            TestContext.Current.CancellationToken);
        var entries = await verificationDb.CreditLedgerEntries
            .Where(entry => entry.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            results.Count(result => result.Outcome == AccountRegistrationOutcome.Registered));
        Assert.Equal(
            1,
            results.Count(result =>
                result.Outcome == AccountRegistrationOutcome.EmailAlreadyRegistered));
        var entry = Assert.Single(entries);
        Assert.Equal(CreditEntryKind.Grant, entry.Kind);
        Assert.Equal(3, entry.Delta);
        Assert.Equal(3, user.CreditBalance);
        Assert.Single(await verificationDb.EmailConfirmationTokens
            .Where(token => token.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verificationDb.AuthenticationEmailOutboxMessages
            .Where(message => message.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

        async Task<AccountRegistrationResult> RegisterAsync(
            string candidateEmail,
            string password)
        {
            await using var db = _fixture.NewDbContext();
            return await NewService(db, passwordHasher)
                .RegisterAsync(
                    candidateEmail,
                    password,
                    TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RegisterAsync_WhenOutboxInsertFails_RollsBackAccountGrantAndToken()
    {
        var email = $"rollback-{Guid.NewGuid():N}@example.test";
        var before = await ReadCountsAsync();

        await using (var db = _fixture.NewDbContext(new ThrowOnOutboxInsertInterceptor()))
        {
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                NewService(db, new PasswordHasher<User>()).RegisterAsync(
                    email,
                    "Known-Test-Password-123!",
                    TestContext.Current.CancellationToken));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        var after = await ReadCountsAsync();
        Assert.Equal(before, after);

        await using var verificationDb = _fixture.NewDbContext();
        var lookup = Assert.Single(TestAccountProtection.CreateService()
            .CreateReadableLookupValues(new AccountEmailAddress(email, email.ToUpperInvariant())));
        Assert.False(await verificationDb.UserEmailLookups.AnyAsync(
            item => item.KeyId == lookup.KeyId
                && item.Value.SequenceEqual(lookup.Value),
            TestContext.Current.CancellationToken));
    }

    private static AccountRegistrationService NewService(
        IndieFi.Web.Data.AppDbContext db,
        IPasswordHasher<User> passwordHasher)
    {
        var timeProvider = new FixedTimeProvider(Now);
        return new AccountRegistrationService(
            db,
            passwordHasher,
            TestAccountProtection.CreateService(),
            new EmailConfirmationTokenService(db, timeProvider),
            new AuthenticationEmailOutboxService(
                db,
                TestAccountProtection.CreateMessageProtectionService(),
                timeProvider));
    }

    private async Task<(long Users, long Lookups, long Ledger, long Tokens, long Outbox)>
        ReadCountsAsync()
    {
        await using var db = _fixture.NewDbContext();
        return (
            await db.Users.LongCountAsync(TestContext.Current.CancellationToken),
            await db.UserEmailLookups.LongCountAsync(TestContext.Current.CancellationToken),
            await db.CreditLedgerEntries.LongCountAsync(TestContext.Current.CancellationToken),
            await db.EmailConfirmationTokens.LongCountAsync(TestContext.Current.CancellationToken),
            await db.AuthenticationEmailOutboxMessages.LongCountAsync(
                TestContext.Current.CancellationToken));
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
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Forced Outbox insert failure.");
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(result);
        }
    }
}
