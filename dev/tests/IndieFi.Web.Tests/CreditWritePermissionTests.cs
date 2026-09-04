using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class CreditWritePermissionTests
{
    private const string InsufficientPrivilege = "42501";

    private readonly PostgreSqlFixture _fixture;

    public CreditWritePermissionTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpdatingCreditBalanceDirectly_IsRefused()
    {
        var (userId, _) = await CreateUserWithGrantAsync();

        await using (var attemptDb = _fixture.NewDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await attemptDb.Database.ExecuteSqlRawAsync(
                    "UPDATE users SET credit_balance = 999 WHERE id = {0}",
                    new object[] { userId },
                    TestContext.Current.CancellationToken));

            Assert.Equal(InsufficientPrivilege, exception.SqlState);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, balance);
    }

    [Fact]
    public async Task InsertingUserWithCreditBalance_IsRefused()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        Assert.True(AccountEmailAddressPolicy.TryCreate(
            email,
            out var address,
            out _));
        var protectedEmail = TestAccountProtection.CreateService().Protect(address!);
        long userCountBefore;

        await using (var attemptDb = _fixture.NewDbContext())
        {
            userCountBefore = await attemptDb.Users.LongCountAsync(
                TestContext.Current.CancellationToken);
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await attemptDb.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO users (
                        email_ciphertext,
                        email_nonce,
                        email_authentication_tag,
                        email_encryption_key_id,
                        password_hash,
                        credit_balance)
                    VALUES ({0}, {1}, {2}, {3}, {4}, 999)
                    """,
                    new object[]
                    {
                        protectedEmail.Ciphertext,
                        protectedEmail.Nonce,
                        protectedEmail.AuthenticationTag,
                        protectedEmail.KeyId,
                        "test-password-hash",
                    },
                    TestContext.Current.CancellationToken));

            Assert.Equal(InsufficientPrivilege, exception.SqlState);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            userCountBefore,
            await verificationDb.Users.LongCountAsync(
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangingLedgerEntry_IsRefused()
    {
        var (userId, ledgerEntryId) = await CreateUserWithGrantAsync();

        await using (var attemptDb = _fixture.NewDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await attemptDb.Database.ExecuteSqlRawAsync(
                    "UPDATE credit_ledger_entries SET delta = 300 WHERE id = {0}",
                    new object[] { ledgerEntryId },
                    TestContext.Current.CancellationToken));

            Assert.Equal(InsufficientPrivilege, exception.SqlState);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var ledgerEntry = await verificationDb.CreditLedgerEntries.SingleAsync(
            entry => entry.Id == ledgerEntryId,
            TestContext.Current.CancellationToken);
        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, ledgerEntry.Delta);
        Assert.Equal(5, balance);
    }

    [Fact]
    public async Task DeletingLedgerEntry_IsRefused()
    {
        var (userId, ledgerEntryId) = await CreateUserWithGrantAsync();

        await using (var attemptDb = _fixture.NewDbContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                await attemptDb.Database.ExecuteSqlRawAsync(
                    "DELETE FROM credit_ledger_entries WHERE id = {0}",
                    new object[] { ledgerEntryId },
                    TestContext.Current.CancellationToken));

            Assert.Equal(InsufficientPrivilege, exception.SqlState);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.True(await verificationDb.CreditLedgerEntries.AnyAsync(
            entry => entry.Id == ledgerEntryId,
            TestContext.Current.CancellationToken));

        var balance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, balance);
    }

    // These tests protect database permissions, not application behavior. A mistaken
    // GRANT in a migration must fail here even if application code never uses that path.
    private async Task<(long UserId, long LedgerEntryId)> CreateUserWithGrantAsync()
    {
        await using var arrangeDb = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            arrangeDb,
            $"{Guid.NewGuid():N}@example.test");
        var ledgerEntry = new CreditLedgerEntry
        {
            User = user,
            Kind = CreditEntryKind.Grant,
            Delta = 5,
        };

        arrangeDb.CreditLedgerEntries.Add(ledgerEntry);
        await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (user.Id, ledgerEntry.Id);
    }
}
