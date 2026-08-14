using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountAuthenticationServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public AccountAuthenticationServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthenticateAsync_WithCorrectPassword_ReturnsUserIdentity()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();
        var userId = await CreateUserAsync(email, password, emailConfirmed: true);

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher,
                    TestAccountProtection.CreateService())
                .AuthenticateAsync($"  {email.ToUpperInvariant()}  ", password);
        }

        Assert.Equal(AccountAuthenticationOutcome.Authenticated, result.Outcome);
        Assert.Equal(userId, result.UserId);
    }

    [Fact]
    public async Task AuthenticateAsync_WithWrongPassword_ReturnsInvalidCredentialsWithoutIdentity()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();

        await CreateUserAsync(email, password, emailConfirmed: false);

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher,
                    TestAccountProtection.CreateService())
                .AuthenticateAsync(email, "Wrong-Test-Password-456!");
        }

        Assert.Equal(AccountAuthenticationOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task AuthenticateAsync_WithCorrectPasswordBeforeEmailConfirmation_RequiresConfirmation()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();
        await CreateUserAsync(email, password, emailConfirmed: false);

        await using var authenticationDb = _fixture.NewDbContext();
        var result = await new AccountAuthenticationService(
                authenticationDb,
                passwordHasher,
                TestAccountProtection.CreateService())
            .AuthenticateAsync(email, password);

        Assert.Equal(AccountAuthenticationOutcome.EmailNotConfirmed, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task AuthenticateAsync_WithUnknownEmail_ReturnsSameInvalidCredentialsResult()
    {
        var passwordHasher = new PasswordHasher<User>();

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher,
                    TestAccountProtection.CreateService())
                .AuthenticateAsync(
                    $"{Guid.NewGuid():N}@example.test",
                    "Any-Test-Password-123!");
        }

        Assert.Equal(AccountAuthenticationOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.UserId);
    }

    private async Task<long> CreateUserAsync(
        string email,
        string password,
        bool emailConfirmed)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email, passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        if (emailConfirmed)
        {
            user.EmailConfirmedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }
}
