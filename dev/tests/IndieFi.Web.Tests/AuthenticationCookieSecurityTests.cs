using System.Net;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationCookieSecurityTests
{
    private readonly PostgreSqlFixture _fixture;

    public AuthenticationCookieSecurityTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Production_UsesSecureAuthenticationAndAntiforgeryCookies()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Production));

        var cookieOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var authentication = cookieOptions.Get(
            CookieAuthenticationDefaults.AuthenticationScheme);
        var temporaryExternal = cookieOptions.Get(
            ExternalAuthenticationDefaults.TemporaryScheme);
        var google = factory.Services
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(ExternalLoginPolicy.GoogleProvider);
        var antiforgery = factory.Services
            .GetRequiredService<IOptions<AntiforgeryOptions>>()
            .Value;

        Assert.True(authentication.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, authentication.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.Always, authentication.Cookie.SecurePolicy);
        Assert.True(temporaryExternal.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, temporaryExternal.Cookie.SecurePolicy);
        Assert.True(google.CorrelationCookie.HttpOnly);
        Assert.Equal(SameSiteMode.None, google.CorrelationCookie.SameSite);
        Assert.Equal(CookieSecurePolicy.Always, google.CorrelationCookie.SecurePolicy);

        Assert.True(antiforgery.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, antiforgery.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.Always, antiforgery.Cookie.SecurePolicy);
    }

    [Fact]
    public void Development_AllowsCookiesOnTheLocalHttpEndpoint()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Development));

        var cookieOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var authentication = cookieOptions.Get(
            CookieAuthenticationDefaults.AuthenticationScheme);
        var temporaryExternal = cookieOptions.Get(
            ExternalAuthenticationDefaults.TemporaryScheme);
        var antiforgery = factory.Services
            .GetRequiredService<IOptions<AntiforgeryOptions>>()
            .Value;

        Assert.Equal(
            CookieSecurePolicy.SameAsRequest,
            authentication.Cookie.SecurePolicy);
        Assert.Equal(
            CookieSecurePolicy.SameAsRequest,
            temporaryExternal.Cookie.SecurePolicy);
        Assert.Equal(
            CookieSecurePolicy.SameAsRequest,
            antiforgery.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task Post_Logout_RequiresAnAntiforgeryToken()
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

        using (var unprotectedForm = new FormUrlEncodedContent([]))
        using (var rejectedResponse = await client.PostAsync(
                   "/Logout",
                   unprotectedForm,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
        }

        string authenticatedHtml;
        using (var authenticatedResponse = await client.GetAsync(
                   "/",
                   TestContext.Current.CancellationToken))
        {
            authenticatedHtml = await authenticatedResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
            Assert.Contains(UserDisplayIdentifier.Format(userId), authenticatedHtml);
        }

        using var protectedForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(authenticatedHtml),
        });
        using var logoutResponse = await client.PostAsync(
            "/Logout",
            protectedForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, logoutResponse.StatusCode);
        Assert.Equal("/", logoutResponse.Headers.Location?.OriginalString);
    }
}
