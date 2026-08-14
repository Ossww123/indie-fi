using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
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
}
