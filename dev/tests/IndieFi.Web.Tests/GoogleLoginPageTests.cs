using System.Net;
using System.Security.Claims;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GoogleLoginPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public GoogleLoginPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PostChallenge_UsesGoogleAndPreservesOnlyLocalReturnUrl()
    {
        const string returnUrl = "/KeyDrops/Detail/123";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var loginPage = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginPage.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(loginHtml),
        });

        using var response = await client.PostAsync(
            "/GoogleLogin?handler=Challenge",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal("accounts.google.com", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        var state = Assert.Single(query["state"]);
        var google = factory.Services
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(ExternalLoginPolicy.GoogleProvider);
        var properties = google.StateDataFormat.Unprotect(state);

        Assert.NotNull(properties);
        Assert.NotNull(properties.RedirectUri);
        Assert.Contains(
            $"returnUrl={Uri.EscapeDataString(returnUrl)}",
            properties.RedirectUri,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                ".AspNetCore.Correlation.",
                StringComparison.Ordinal)
                && value.Contains("samesite=none", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Callback_WithLinkedGoogleSubject_IssuesApplicationSessionAndClearsTemporaryCookie()
    {
        const string subject = "linked-google-subject";
        const long sessionVersion = 29;
        const string returnUrl = "/KeyDrops/Detail/456";
        var userId = await CreateLinkedUserAsync(subject, sessionVersion);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            $"/GoogleLogin?handler=Callback&returnUrl={Uri.EscapeDataString(returnUrl)}",
            subject,
            email: "linked@example.test",
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(returnUrl, response.Headers.Location?.OriginalString);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(
            setCookies,
            value => value.StartsWith(
                $"{ExternalAuthenticationDefaults.TemporaryCookieName}=",
                StringComparison.Ordinal)
                && value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        var applicationTicket = ReadApplicationTicket(factory, setCookies);
        Assert.Equal(
            userId.ToString(),
            applicationTicket.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(
            sessionVersion.ToString(),
            applicationTicket.Principal.FindFirstValue(
                AuthenticationClaimTypes.SessionVersion));
        Assert.DoesNotContain(
            applicationTicket.Principal.Claims,
            claim => claim.Type == ClaimTypes.Email
                || claim.Value.Contains("linked@example.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Callback_WithExternalReturnUrl_RedirectsHome()
    {
        const string subject = "external-return-google-subject";
        await CreateLinkedUserAsync(subject);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback&returnUrl=https%3A%2F%2Fexample.com%2Fphishing",
            subject,
            email: "external-return@example.test",
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("unlinked-google-subject", true)]
    [InlineData("invalid-google-claims", false)]
    public async Task Callback_WithoutLinkedAccountOrVerifiedEmail_FailsWithoutApplicationSession(
        string subject,
        bool emailVerified)
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email: "unlinked@example.test",
            emailVerified);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.OriginalString);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Contains(
            setCookies,
            value => value.StartsWith(
                $"{ExternalAuthenticationDefaults.TemporaryCookieName}=",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            setCookies,
            value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLogin_AfterGoogleRemoteFailure_ShowsSeparateGoogleMessage()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Login?googleError=remote",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Google 인증을 완료하지 못했습니다", html);
        Assert.Contains("이메일 로그인은 계속 사용할 수 있습니다", html);
        Assert.Contains("name=\"Email\"", html);
    }

    private async Task<long> CreateLinkedUserAsync(
        string subject,
        long sessionVersion = 1)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"linked-google-{Guid.NewGuid():N}@example.test");
        user.EmailConfirmedAt = DateTimeOffset.UtcNow;
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ExternalLogins.Add(new ExternalLogin
        {
            UserId = user.Id,
            Provider = ExternalLoginPolicy.GoogleProvider,
            ProviderUserId = subject,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private static HttpRequestMessage CreateCallbackRequest(
        WebAppFactory factory,
        string requestUri,
        string subject,
        string email,
        bool emailVerified)
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ExternalAuthenticationDefaults.TemporaryScheme);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim(ClaimTypes.Email, email),
            new Claim(
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim,
                emailVerified.ToString()),
        };
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                ExternalAuthenticationDefaults.TemporaryScheme)),
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(
                    ExternalAuthenticationDefaults.TemporaryLifetime),
            },
            ExternalAuthenticationDefaults.TemporaryScheme);
        var protectedTicket = options.TicketDataFormat.Protect(ticket);
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add(
            "Cookie",
            $"{ExternalAuthenticationDefaults.TemporaryCookieName}={protectedTicket}");
        return request;
    }

    private static AuthenticationTicket ReadApplicationTicket(
        WebAppFactory factory,
        IEnumerable<string> setCookies)
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieName = Assert.IsType<string>(options.Cookie.Name);
        var prefix = $"{cookieName}=";
        var setCookie = Assert.Single(
            setCookies,
            value => value.StartsWith(prefix, StringComparison.Ordinal));
        var protectedTicket = setCookie[prefix.Length..].Split(';', 2)[0];
        return Assert.IsType<AuthenticationTicket>(
            options.TicketDataFormat.Unprotect(protectedTicket));
    }
}
