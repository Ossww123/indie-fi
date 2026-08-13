using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class CreditBalanceTriggerTests
{
    private readonly PostgreSqlFixture _fixture;

    public CreditBalanceTriggerTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task InsertingLedgerEntries_MakesTheDatabaseMaintainTheBalance()
    {
        long userId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = new User
            {
                Email = $"{Guid.NewGuid():N}@example.test",
                PasswordHash = "test-password-hash",
            };
            user.NormalizedEmail = user.Email.ToUpperInvariant();

            arrangeDb.Users.Add(user);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        await using (var zeroBalanceDb = _fixture.NewDbContext())
        {
            var balance = await zeroBalanceDb.Users
                .Where(user => user.Id == userId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, balance);
        }

        // Application code never writes the balance; fresh database reads show that
        // it follows the ledger entries applied by the trigger.
        await using (var grantDb = _fixture.NewDbContext())
        {
            grantDb.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                UserId = userId,
                Kind = CreditEntryKind.Grant,
                Delta = 5,
            });
            await grantDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var grantedBalanceDb = _fixture.NewDbContext())
        {
            var balance = await grantedBalanceDb.Users
                .Where(user => user.Id == userId)
                .Select(user => user.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(5, balance);
        }

        await using (var spendDb = _fixture.NewDbContext())
        {
            spendDb.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                UserId = userId,
                Kind = CreditEntryKind.Spend,
                Delta = -2,
            });
            await spendDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var finalBalance = await verificationDb.Users
            .Where(user => user.Id == userId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken);
        var ledgerSum = await verificationDb.CreditLedgerEntries
            .Where(entry => entry.UserId == userId)
            .SumAsync(
                entry => entry.Delta,
                TestContext.Current.CancellationToken);

        Assert.Equal(3, finalBalance);
        Assert.Equal(ledgerSum, finalBalance);
    }
}
