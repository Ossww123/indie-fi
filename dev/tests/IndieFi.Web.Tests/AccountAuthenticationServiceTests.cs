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
        long userId;

        await using (var registrationDb = _fixture.NewDbContext())
        {
            var registration = await new AccountRegistrationService(
                    registrationDb,
                    passwordHasher)
                .RegisterAsync(email, password);
            userId = registration.UserId!.Value;
        }

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher)
                .AuthenticateAsync($"  {email.ToUpperInvariant()}  ", password);
        }

        Assert.Equal(AccountAuthenticationOutcome.Authenticated, result.Outcome);
        Assert.Equal(userId, result.UserId);
        Assert.Equal(email, result.Email);
    }

    [Fact]
    public async Task AuthenticateAsync_WithWrongPassword_ReturnsInvalidCredentialsWithoutIdentity()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";
        var passwordHasher = new PasswordHasher<User>();

        await using (var registrationDb = _fixture.NewDbContext())
        {
            await new AccountRegistrationService(registrationDb, passwordHasher)
                .RegisterAsync(email, password);
        }

        AccountAuthenticationResult result;
        await using (var authenticationDb = _fixture.NewDbContext())
        {
            result = await new AccountAuthenticationService(
                    authenticationDb,
                    passwordHasher)
                .AuthenticateAsync(email, "Wrong-Test-Password-456!");
        }

        Assert.Equal(AccountAuthenticationOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.UserId);
        Assert.Null(result.Email);
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
                    passwordHasher)
                .AuthenticateAsync(
                    $"{Guid.NewGuid():N}@example.test",
                    "Any-Test-Password-123!");
        }

        Assert.Equal(AccountAuthenticationOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.UserId);
        Assert.Null(result.Email);
    }
}
