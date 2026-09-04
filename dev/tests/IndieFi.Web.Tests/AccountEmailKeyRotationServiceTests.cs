using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountEmailKeyRotationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public AccountEmailKeyRotationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RotateBatchAsync_ReencryptsAndBackfillsWithoutRemovingOldLookup()
    {
        var firstEmail = $"rotate-first-{Guid.NewGuid():N}@example.test";
        var secondEmail = $"rotate-second-{Guid.NewGuid():N}@example.test";
        long firstUserId;
        long secondUserId;
        byte[] firstOldCiphertext;

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var first = TestAccountProtection.AddUser(setupDb, firstEmail);
            var second = TestAccountProtection.AddUser(setupDb, secondEmail);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstUserId = first.Id;
            secondUserId = second.Id;
            firstOldCiphertext = first.EmailCiphertext.ToArray();
        }

        var provider = CreateRotationProvider();
        await using var rotationDb = _fixture.NewOwnerDbContext();
        var service = new AccountEmailKeyRotationService(rotationDb, provider);

        var firstBatch = await service.RotateBatchAsync(
            firstUserId - 1,
            batchSize: 1,
            TestContext.Current.CancellationToken);
        var secondBatch = await service.RotateBatchAsync(
            firstBatch.NextUserId,
            batchSize: 1,
            TestContext.Current.CancellationToken);
        var completedBatch = await service.RotateBatchAsync(
            secondBatch.NextUserId,
            batchSize: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(firstUserId, firstBatch.NextUserId);
        Assert.Equal(1, firstBatch.ReencryptedCount);
        Assert.Equal(1, firstBatch.AddedLookupCount);
        Assert.Equal(secondUserId, secondBatch.NextUserId);
        Assert.Equal(1, secondBatch.ReencryptedCount);
        Assert.Equal(1, secondBatch.AddedLookupCount);
        Assert.Equal(0, completedBatch.ScannedCount);

        rotationDb.ChangeTracker.Clear();
        var users = await rotationDb.Users
            .Where(user => user.Id == firstUserId || user.Id == secondUserId)
            .OrderBy(user => user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var lookups = await rotationDb.UserEmailLookups
            .Where(lookup => lookup.UserId == firstUserId || lookup.UserId == secondUserId)
            .OrderBy(lookup => lookup.UserId)
            .ThenBy(lookup => lookup.KeyId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(users, user => Assert.Equal("email-test-v2", user.EmailEncryptionKeyId));
        Assert.NotEqual(firstOldCiphertext, users[0].EmailCiphertext);
        Assert.All(
            users,
            user => Assert.Equal(
                user.Id == firstUserId ? firstEmail : secondEmail,
                new EmailProtectionService(provider).Unprotect(
                    new ProtectedEmailAddress(
                        user.EmailCiphertext,
                        user.EmailNonce,
                        user.EmailAuthenticationTag,
                        user.EmailEncryptionKeyId))));
        Assert.All(
            users,
            user => Assert.Equal(
                ["lookup-test-v1", "lookup-test-v2"],
                lookups
                    .Where(lookup => lookup.UserId == user.Id)
                    .Select(lookup => lookup.KeyId)));

        var verification = await service.VerifyBatchAsync(
            firstUserId - 1,
            batchSize: 10,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, verification.ScannedCount);

        rotationDb.ChangeTracker.Clear();
        var repeated = await service.RotateBatchAsync(
            firstUserId - 1,
            batchSize: 10,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, repeated.ScannedCount);
        Assert.Equal(0, repeated.ReencryptedCount);
        Assert.Equal(0, repeated.AddedLookupCount);
    }

    [Fact]
    public async Task RotateBatchAsync_AfterProcessRestart_ResumesWithoutRepeatingChanges()
    {
        var firstEmail = $"restart-first-{Guid.NewGuid():N}@example.test";
        var secondEmail = $"restart-second-{Guid.NewGuid():N}@example.test";
        long firstUserId;
        long secondUserId;

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var first = TestAccountProtection.AddUser(setupDb, firstEmail);
            var second = TestAccountProtection.AddUser(setupDb, secondEmail);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstUserId = first.Id;
            secondUserId = second.Id;
        }

        var provider = CreateRotationProvider();
        await using (var interruptedDb = _fixture.NewOwnerDbContext())
        {
            var interruptedService = new AccountEmailKeyRotationService(
                interruptedDb,
                provider);
            var firstBatch = await interruptedService.RotateBatchAsync(
                firstUserId - 1,
                batchSize: 1,
                TestContext.Current.CancellationToken);
            Assert.Equal(firstUserId, firstBatch.NextUserId);
            Assert.Equal(1, firstBatch.ReencryptedCount);
        }

        await using var resumedDb = _fixture.NewOwnerDbContext();
        var resumedService = new AccountEmailKeyRotationService(resumedDb, provider);
        var resumed = await resumedService.RotateBatchAsync(
            firstUserId - 1,
            batchSize: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(secondUserId, resumed.NextUserId);
        Assert.Equal(2, resumed.ScannedCount);
        Assert.Equal(1, resumed.ReencryptedCount);
        Assert.Equal(1, resumed.AddedLookupCount);

        var verification = await resumedService.VerifyBatchAsync(
            firstUserId - 1,
            batchSize: 10,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, verification.ScannedCount);
    }

    [Fact]
    public async Task RotateBatchAsync_WhenLaterLookupIsInvalid_RollsBackWholeBatch()
    {
        var firstEmail = $"rollback-first-{Guid.NewGuid():N}@example.test";
        var secondEmail = $"rollback-second-{Guid.NewGuid():N}@example.test";
        long firstUserId;
        long secondUserId;

        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var first = TestAccountProtection.AddUser(setupDb, firstEmail);
            var second = TestAccountProtection.AddUser(setupDb, secondEmail);
            setupDb.UserEmailLookups.Add(new UserEmailLookup
            {
                User = second,
                KeyId = "lookup-test-v2",
                Value = Enumerable.Repeat((byte)99, 32).ToArray(),
            });
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstUserId = first.Id;
            secondUserId = second.Id;
        }

        await using (var rotationDb = _fixture.NewOwnerDbContext())
        {
            var service = new AccountEmailKeyRotationService(
                rotationDb,
                CreateRotationProvider());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RotateBatchAsync(
                    firstUserId - 1,
                    batchSize: 2,
                    TestContext.Current.CancellationToken));
        }

        await using var verificationDb = _fixture.NewOwnerDbContext();
        var users = await verificationDb.Users
            .Where(user => user.Id == firstUserId || user.Id == secondUserId)
            .OrderBy(user => user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var newLookups = await verificationDb.UserEmailLookups
            .Where(lookup =>
                (lookup.UserId == firstUserId || lookup.UserId == secondUserId)
                && lookup.KeyId == "lookup-test-v2")
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(users, user => Assert.Equal("email-test-v1", user.EmailEncryptionKeyId));
        Assert.DoesNotContain(newLookups, lookup => lookup.UserId == firstUserId);
        var invalidLookup = Assert.Single(
            newLookups,
            lookup => lookup.UserId == secondUserId);
        Assert.Equal(Enumerable.Repeat((byte)99, 32), invalidLookup.Value);

        verificationDb.UserEmailLookups.Remove(invalidLookup);
        await verificationDb.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RetireLookupKeyAsync_RequiresInactiveKeyAndVerifiedCurrentData()
    {
        var email = $"retire-{Guid.NewGuid():N}@example.test";
        long userId;
        await using (var setupDb = _fixture.NewOwnerDbContext())
        {
            var user = TestAccountProtection.AddUser(setupDb, email);
            await setupDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        var rotationProvider = CreateRotationProvider();
        await using (var rotationDb = _fixture.NewOwnerDbContext())
        {
            var service = new AccountEmailKeyRotationService(
                rotationDb,
                rotationProvider);
            long cursor = 0;
            while (true)
            {
                var result = await service.RotateBatchAsync(
                    cursor,
                    batchSize: 100,
                    TestContext.Current.CancellationToken);
                if (result.ScannedCount == 0)
                {
                    break;
                }

                cursor = result.NextUserId;
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RetireLookupKeyAsync(
                    "lookup-test-v1",
                    batchSize: 100,
                    TestContext.Current.CancellationToken));
        }

        await using var retirementDb = _fixture.NewOwnerDbContext();
        var retirementService = new AccountEmailKeyRotationService(
            retirementDb,
            CreateRetiredProvider());
        var deleted = await retirementService.RetireLookupKeyAsync(
            "lookup-test-v1",
            batchSize: 100,
            TestContext.Current.CancellationToken);
        var repeated = await retirementService.RetireLookupKeyAsync(
            "lookup-test-v1",
            batchSize: 100,
            TestContext.Current.CancellationToken);

        Assert.True(deleted >= 1);
        Assert.Equal(0, repeated);
        Assert.False(await retirementDb.UserEmailLookups.AnyAsync(
            lookup => lookup.KeyId == "lookup-test-v1",
            TestContext.Current.CancellationToken));
        Assert.True(await retirementDb.UserEmailLookups.AnyAsync(
            lookup => lookup.UserId == userId && lookup.KeyId == "lookup-test-v2",
            TestContext.Current.CancellationToken));
    }

    private static IKeyProvider CreateRotationProvider()
    {
        var values = TestAccountProtection.ConfigurationValues();
        values["AccountProtection:Purposes:account-email-encryption:CurrentKeyId"] =
            "email-test-v2";
        values["AccountProtection:Purposes:account-email-encryption:ReadKeyIds:0"] =
            "email-test-v1";
        values["AccountProtection:Purposes:account-email-encryption:ReadKeyIds:1"] =
            "email-test-v2";
        values["AccountProtection:Purposes:account-email-encryption:WriteKeyIds:0"] =
            "email-test-v2";
        values["AccountProtection:Purposes:account-email-encryption:Keys:email-test-v2"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)31, 32).ToArray());

        values["AccountProtection:Purposes:account-email-lookup:CurrentKeyId"] =
            "lookup-test-v2";
        values["AccountProtection:Purposes:account-email-lookup:ReadKeyIds:0"] =
            "lookup-test-v1";
        values["AccountProtection:Purposes:account-email-lookup:ReadKeyIds:1"] =
            "lookup-test-v2";
        values["AccountProtection:Purposes:account-email-lookup:WriteKeyIds:0"] =
            "lookup-test-v1";
        values["AccountProtection:Purposes:account-email-lookup:WriteKeyIds:1"] =
            "lookup-test-v2";
        values["AccountProtection:Purposes:account-email-lookup:Keys:lookup-test-v2"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)32, 32).ToArray());

        return new ConfigurationKeyProvider(
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build());
    }

    private static IKeyProvider CreateRetiredProvider()
    {
        var values = TestAccountProtection.ConfigurationValues();
        values["AccountProtection:Purposes:account-email-encryption:CurrentKeyId"] =
            "email-test-v2";
        values["AccountProtection:Purposes:account-email-encryption:ReadKeyIds:0"] =
            "email-test-v2";
        values["AccountProtection:Purposes:account-email-encryption:WriteKeyIds:0"] =
            "email-test-v2";
        values["AccountProtection:Purposes:account-email-encryption:Keys:email-test-v2"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)31, 32).ToArray());

        values["AccountProtection:Purposes:account-email-lookup:CurrentKeyId"] =
            "lookup-test-v2";
        values["AccountProtection:Purposes:account-email-lookup:ReadKeyIds:0"] =
            "lookup-test-v2";
        values["AccountProtection:Purposes:account-email-lookup:WriteKeyIds:0"] =
            "lookup-test-v2";
        values["AccountProtection:Purposes:account-email-lookup:Keys:lookup-test-v2"] =
            Convert.ToBase64String(Enumerable.Repeat((byte)32, 32).ToArray());

        return new ConfigurationKeyProvider(
            new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build());
    }
}
