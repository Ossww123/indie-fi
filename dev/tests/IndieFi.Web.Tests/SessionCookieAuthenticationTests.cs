using System.Globalization;
using System.Net;
using System.Security.Claims;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class SessionCookieAuthenticationTests
{
    private readonly PostgreSqlFixture _fixture;

    public SessionCookieAuthenticationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_AfterSessionVersionChanges_RejectsEveryExistingCookie()
    {
        var account = await CreateAccountAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(firstClient, account.Email, account.Password);
        await PageTestSupport.LoginAsync(secondClient, account.Email, account.Password);

        await AssertAuthenticatedAsync(firstClient, account.UserId);
        await AssertAuthenticatedAsync(secondClient, account.UserId);

        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            var updatedRows = await ownerDb.Users
                .Where(user => user.Id == account.UserId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        user => user.SessionVersion,
                        user => user.SessionVersion + 1),
                    TestContext.Current.CancellationToken);
            Assert.Equal(1, updatedRows);
        }

        await AssertRejectedAndDeletedAsync(factory, firstClient, account.UserId);
        await AssertRejectedAndDeletedAsync(factory, secondClient, account.UserId);
    }

    [Fact]
    public async Task Get_AfterAccountIsSuspended_RejectsTheExistingCookie()
    {
        var account = await CreateAccountAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);

        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            var updatedRows = await ownerDb.Users
                .Where(user => user.Id == account.UserId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        user => user.AccountStatus,
                        AccountStatus.IndefinitelySuspended),
                    TestContext.Current.CancellationToken);
            Assert.Equal(1, updatedRows);
        }

        await AssertRejectedAndDeletedAsync(factory, client, account.UserId);
    }

    [Fact]
    public async Task Get_AfterAccountIsDeleted_RejectsTheExistingCookie()
    {
        var account = await CreateAccountAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);

        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            var deletedRows = await ownerDb.Users
                .Where(user => user.Id == account.UserId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, deletedRows);
        }

        await AssertRejectedAndDeletedAsync(factory, client, account.UserId);
    }

    [Fact]
    public Task Get_WithMissingSessionVersionClaim_RejectsTheCookie()
    {
        return AssertMalformedClaimsRejectedAsync(MalformedClaims.MissingSessionVersion);
    }

    [Fact]
    public Task Get_WithDuplicateSessionVersionClaims_RejectsTheCookie()
    {
        return AssertMalformedClaimsRejectedAsync(MalformedClaims.DuplicateSessionVersion);
    }

    [Fact]
    public Task Get_WithNonNumericSessionVersionClaim_RejectsTheCookie()
    {
        return AssertMalformedClaimsRejectedAsync(MalformedClaims.NonNumericSessionVersion);
    }

    [Fact]
    public async Task Get_WithTamperedProtectedCookie_DoesNotAuthenticate()
    {
        var account = await CreateAccountAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var cookie = ProtectCookie(
            factory,
            CreateValidClaims(account.UserId, sessionVersion: 1));
        var tamperedCharacters = cookie.Value.ToCharArray();
        var tamperedIndex = tamperedCharacters.Length / 2;
        tamperedCharacters[tamperedIndex] =
            tamperedCharacters[tamperedIndex] == 'A' ? 'B' : 'A';
        var tamperedValue = new string(tamperedCharacters);

        using var response = await GetWithCookieAsync(
            client,
            cookie.Name,
            tamperedValue);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(UserDisplayIdentifier.Format(account.UserId), html);
    }

    private async Task AssertMalformedClaimsRejectedAsync(MalformedClaims malformedClaims)
    {
        var account = await CreateAccountAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var claims = CreateValidClaims(account.UserId, sessionVersion: 1);

        switch (malformedClaims)
        {
            case MalformedClaims.MissingSessionVersion:
                claims.RemoveAll(claim =>
                    claim.Type == AuthenticationClaimTypes.SessionVersion);
                break;
            case MalformedClaims.DuplicateSessionVersion:
                claims.Add(new Claim(AuthenticationClaimTypes.SessionVersion, "1"));
                break;
            case MalformedClaims.NonNumericSessionVersion:
                claims.RemoveAll(claim =>
                    claim.Type == AuthenticationClaimTypes.SessionVersion);
                claims.Add(new Claim(
                    AuthenticationClaimTypes.SessionVersion,
                    "not-a-number"));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(malformedClaims),
                    malformedClaims,
                    null);
        }

        var cookie = ProtectCookie(factory, claims);
        using var response = await GetWithCookieAsync(
            client,
            cookie.Name,
            cookie.Value);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(UserDisplayIdentifier.Format(account.UserId), html);
        AssertCookieDeleted(response, cookie.Name);
    }

    private async Task<Account> CreateAccountAsync()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var userId = (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
        return new Account(email, password, userId);
    }

    private static List<Claim> CreateValidClaims(long userId, long sessionVersion)
    {
        return
        [
            new Claim(
                ClaimTypes.NameIdentifier,
                userId.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, UserDisplayIdentifier.Format(userId)),
            new Claim(
                AuthenticationClaimTypes.SessionVersion,
                sessionVersion.ToString(CultureInfo.InvariantCulture)),
        ];
    }

    private static ProtectedCookie ProtectCookie(
        WebAppFactory factory,
        IEnumerable<Claim> claims)
    {
        var cookieOptions = GetCookieOptions(factory);
        var cookieName = Assert.IsType<string>(cookieOptions.Cookie.Name);
        var now = DateTimeOffset.UtcNow;
        var identity = new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IssuedUtc = now,
                ExpiresUtc = now.AddHours(1),
            },
            CookieAuthenticationDefaults.AuthenticationScheme);

        return new ProtectedCookie(
            cookieName,
            cookieOptions.TicketDataFormat.Protect(ticket));
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(
        HttpClient client,
        string cookieName,
        string cookieValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{cookieName}={cookieValue}");
        return await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
    }

    private static async Task AssertAuthenticatedAsync(HttpClient client, long userId)
    {
        using var response = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(UserDisplayIdentifier.Format(userId), html);
    }

    private static async Task AssertRejectedAndDeletedAsync(
        WebAppFactory factory,
        HttpClient client,
        long userId)
    {
        using var response = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(UserDisplayIdentifier.Format(userId), html);

        var cookieName = Assert.IsType<string>(GetCookieOptions(factory).Cookie.Name);
        AssertCookieDeleted(response, cookieName);
    }

    private static CookieAuthenticationOptions GetCookieOptions(WebAppFactory factory)
    {
        return factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static void AssertCookieDeleted(
        HttpResponseMessage response,
        string cookieName)
    {
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{cookieName}=;", StringComparison.Ordinal));
    }

    private sealed record Account(string Email, string Password, long UserId);

    private sealed record ProtectedCookie(string Name, string Value);

    private enum MalformedClaims
    {
        MissingSessionVersion,
        DuplicateSessionVersion,
        NonNumericSessionVersion,
    }
}
