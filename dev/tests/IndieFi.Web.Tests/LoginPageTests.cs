using System.Net;
using System.Security.Claims;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class LoginPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public LoginPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Login_WithLocalReturnUrl_RedirectsBackToThatPage()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        const string returnUrl = "/KeyDrops/Detail/123";

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var loginPageResponse = await client.GetAsync(
            $"/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginPageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(loginHtml);

        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var loginResponse = await client.PostAsync(
            "/Login",
            loginForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.NotNull(loginResponse.Headers.Location);
        Assert.Equal(returnUrl, loginResponse.Headers.Location.OriginalString);
    }

    // An external redirect after a trusted login would hand users directly to an attacker's phishing site.
    [Fact]
    public async Task Post_Login_WithExternalReturnUrl_IgnoresItAndRedirectsHome()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        const string returnUrl = "https://example.com/phishing";

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var loginPageResponse = await client.GetAsync(
            $"/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginPageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(loginHtml);

        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var loginResponse = await client.PostAsync(
            "/Login",
            loginForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        var location = loginResponse.Headers.Location;
        Assert.NotNull(location);
        Assert.Equal("/", location.OriginalString);
        Assert.NotEqual(returnUrl, location.OriginalString);
        Assert.DoesNotContain("example.com", location.OriginalString);
    }

    [Fact]
    public async Task Get_Layout_AfterLogin_ShowsUserIdentifierWithoutEmail()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = (await TestAccountProtection.FindUserAsync(
                db,
                email,
                TestContext.Current.CancellationToken)).Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(UserDisplayIdentifier.Format(userId), html);
        Assert.DoesNotContain(email, html);
    }

    [Fact]
    public async Task Post_Login_IssuesOnlyTheRequiredClaimsWithTheCurrentSessionVersion()
    {
        const long sessionVersion = 37;
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture,
            sessionVersion: sessionVersion);
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = (await TestAccountProtection.FindUserAsync(
                db,
                email,
                TestContext.Current.CancellationToken)).Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var loginPageResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginPageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(loginHtml),
        });
        using var loginResponse = await client.PostAsync(
            "/Login",
            loginForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);

        var cookieOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieName = Assert.IsType<string>(cookieOptions.Cookie.Name);
        var cookiePrefix = $"{cookieName}=";
        var setCookie = Assert.Single(
            loginResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(cookiePrefix, StringComparison.Ordinal));
        var protectedTicket = setCookie[cookiePrefix.Length..].Split(';', 2)[0];
        var ticket = cookieOptions.TicketDataFormat.Unprotect(protectedTicket);

        Assert.NotNull(ticket);
        var claims = ticket.Principal.Claims.ToList();
        Assert.Equal(3, claims.Count);
        Assert.Contains(
            claims,
            claim => claim.Type == ClaimTypes.NameIdentifier
                && claim.Value == userId.ToString());
        Assert.Contains(
            claims,
            claim => claim.Type == ClaimTypes.Name
                && claim.Value == UserDisplayIdentifier.Format(userId));
        Assert.Contains(
            claims,
            claim => claim.Type == AuthenticationClaimTypes.SessionVersion
                && claim.Value == sessionVersion.ToString());
        Assert.DoesNotContain(
            claims,
            claim => claim.Type == ClaimTypes.Email
                || claim.Value.Contains(email, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Post_Login_BeforeEmailConfirmation_ShowsConfirmationRequiredWithoutCookie()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture,
            emailConfirmed: false);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var pageResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(pageHtml);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var response = await client.PostAsync(
            "/Login",
            form,
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var decodedHtml = WebUtility.HtmlDecode(html);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("이메일 확인이 필요합니다", decodedHtml);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
        {
            Assert.DoesNotContain(
                setCookieValues,
                value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        }
    }
}
