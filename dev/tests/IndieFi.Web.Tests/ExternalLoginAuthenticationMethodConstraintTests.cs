using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ExternalLoginAuthenticationMethodConstraintTests
{
    private const string AuthenticationMethodConstraint =
        "CT_users_authentication_method_required";

    private readonly PostgreSqlFixture _fixture;

    public ExternalLoginAuthenticationMethodConstraintTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Commit_PasswordlessUserAndGoogleLoginInSameTransaction_Succeeds()
    {
        var userId = await CreatePasswordlessGoogleUserAsync();

        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        var login = await db.ExternalLogins.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);

        Assert.Null(user.PasswordHash);
        Assert.Equal(ExternalLoginPolicy.GoogleProvider, login.Provider);
        Assert.False(string.IsNullOrWhiteSpace(login.ProviderUserId));
    }

    [Fact]
    public async Task Commit_PasswordlessUserWithoutExternalLogin_IsRejected()
    {
        await using var db = _fixture.NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        TestAccountProtection.AddUser(
            db,
            $"no-auth-{Guid.NewGuid():N}@example.test",
            passwordHash: null);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AssertConstraintViolationAsync(
            () => transaction.CommitAsync(TestContext.Current.CancellationToken),
            AuthenticationMethodConstraint);
    }

    [Fact]
    public async Task Commit_RemovingLastLoginFromPasswordlessUser_IsRejected()
    {
        var userId = await CreatePasswordlessGoogleUserAsync();

        await using var db = _fixture.NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var login = await db.ExternalLogins.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        db.ExternalLogins.Remove(login);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AssertConstraintViolationAsync(
            () => transaction.CommitAsync(TestContext.Current.CancellationToken),
            AuthenticationMethodConstraint);
    }

    [Fact]
    public async Task Commit_SettingPasswordWhileRemovingLastLogin_Succeeds()
    {
        var userId = await CreatePasswordlessGoogleUserAsync();

        await using (var db = _fixture.NewDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            var user = await db.Users.SingleAsync(
                item => item.Id == userId,
                TestContext.Current.CancellationToken);
            var login = await db.ExternalLogins.SingleAsync(
                item => item.UserId == userId,
                TestContext.Current.CancellationToken);

            user.PasswordHash = "replacement-password-hash";
            db.ExternalLogins.Remove(login);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var savedPasswordHash = await verificationDb.Users
            .Where(item => item.Id == userId)
            .Select(item => item.PasswordHash)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("replacement-password-hash", savedPasswordHash);
        Assert.False(await verificationDb.ExternalLogins.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Commit_RemovingOnlyPasswordFromEmailUser_IsRejected()
    {
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(
                arrangeDb,
                $"remove-password-{Guid.NewGuid():N}@example.test");
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        await using var db = _fixture.NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var account = await db.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        account.PasswordHash = null;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await AssertConstraintViolationAsync(
            () => transaction.CommitAsync(TestContext.Current.CancellationToken),
            AuthenticationMethodConstraint);
    }

    [Theory]
    [InlineData("GitHub", "valid-subject", "CK_external_logins_supported_provider")]
    [InlineData("Google", "   ", "CK_external_logins_provider_user_id_not_blank")]
    public async Task SaveChanges_WithUnsupportedProviderOrBlankSubject_IsRejected(
        string provider,
        string providerUserId,
        string constraintName)
    {
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(
                arrangeDb,
                $"invalid-external-{Guid.NewGuid():N}@example.test");
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        await using var db = _fixture.NewDbContext();
        db.ExternalLogins.Add(new ExternalLogin
        {
            UserId = userId,
            Provider = provider,
            ProviderUserId = providerUserId,
        });

        await AssertConstraintViolationAsync(
            () => db.SaveChangesAsync(TestContext.Current.CancellationToken),
            constraintName);
    }

    private async Task<long> CreatePasswordlessGoogleUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var user = TestAccountProtection.AddUser(
            db,
            $"google-only-{Guid.NewGuid():N}@example.test",
            passwordHash: null);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id,
            Provider = ExternalLoginPolicy.GoogleProvider,
            ProviderUserId = $"google-sub-{Guid.NewGuid():N}",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private static async Task AssertConstraintViolationAsync(
        Func<Task> action,
        string constraintName)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(action);
        var postgresException = FindPostgresException(exception);

        Assert.NotNull(postgresException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal(constraintName, postgresException.ConstraintName);
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        return null;
    }
}
