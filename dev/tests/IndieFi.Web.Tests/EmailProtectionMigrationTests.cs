using IndieFi.AccountMigration;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailProtectionMigrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public EmailProtectionMigrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BackfillAsync_ProtectsLegacyUsersAndCanResumeSafely()
    {
        var firstEmail = $"legacy-{Guid.NewGuid():N}@example.test";
        var secondEmail = $"legacy-{Guid.NewGuid():N}@example.test";
        long firstUserId;

        await using (var arrangeDb = _fixture.NewOwnerDbContext())
        {
            var firstUser = CreateLegacyUser(firstEmail);
            arrangeDb.Users.AddRange(firstUser, CreateLegacyUser(secondEmail));
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            firstUserId = firstUser.Id;
        }

        var emailProtection = TestAccountProtection.CreateService();

        await using (var migrationDb = _fixture.NewOwnerDbContext())
        {
            var migration = new EmailProtectionMigration(
                migrationDb,
                emailProtection);

            var firstRun = await migration.BackfillAsync(
                1,
                TestContext.Current.CancellationToken);
            var secondRun = await migration.BackfillAsync(
                1,
                TestContext.Current.CancellationToken);
            var verification = await migration.VerifyAsync(
                1,
                TestContext.Current.CancellationToken);

            Assert.True(firstRun.ProtectedUserCount >= 2);
            Assert.True(firstRun.AddedLookupCount >= 2);
            Assert.Equal(0, firstRun.ProblemUserCount);
            Assert.Equal(0, secondRun.ProtectedUserCount);
            Assert.Equal(0, secondRun.AddedLookupCount);
            Assert.Equal(0, secondRun.ProblemUserCount);
            Assert.True(verification.IsSuccessful);
        }

        byte[] originalCiphertext;
        await using (var corruptDb = _fixture.NewOwnerDbContext())
        {
            var user = await corruptDb.Users.SingleAsync(
                item => item.Id == firstUserId,
                TestContext.Current.CancellationToken);
            originalCiphertext = user.EmailCiphertext!.ToArray();
            var corruptedCiphertext = originalCiphertext.ToArray();
            corruptedCiphertext[0] ^= 0xff;
            user.EmailCiphertext = corruptedCiphertext;
            await corruptDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        EmailProtectionVerificationResult corruptedVerification;
        await using (var verificationDb = _fixture.NewOwnerDbContext())
        {
            var migration = new EmailProtectionMigration(
                verificationDb,
                emailProtection);
            corruptedVerification = await migration.VerifyAsync(
                100,
                TestContext.Current.CancellationToken);
        }

        await using (var restoreDb = _fixture.NewOwnerDbContext())
        {
            var user = await restoreDb.Users.SingleAsync(
                item => item.Id == firstUserId,
                TestContext.Current.CancellationToken);
            user.EmailCiphertext = originalCiphertext;
            await restoreDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.False(corruptedVerification.IsSuccessful);
        Assert.Equal(1, corruptedVerification.UnreadableProtectionCount);
    }

    private static User CreateLegacyUser(string email)
    {
        return new User
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = "legacy-password-hash",
        };
    }
}
