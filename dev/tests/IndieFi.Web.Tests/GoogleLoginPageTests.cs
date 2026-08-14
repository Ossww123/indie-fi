using System.Net;
using System.Security.Claims;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Callback_WithoutVerifiedEmail_FailsWithoutApplicationSession()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            "invalid-google-claims",
            email: "unlinked@example.test",
            emailVerified: false);

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
    public async Task Callback_WithNewSubjectAndEmail_CreatesPasswordlessConfirmedAccountAndStartingCredit()
    {
        var subject = $"new-google-{Guid.NewGuid():N}";
        var email = $"new-google-{Guid.NewGuid():N}@example.test";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email,
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        var login = await db.ExternalLogins.SingleAsync(
            item => item.Provider == ExternalLoginPolicy.GoogleProvider
                && item.ProviderUserId == subject,
            TestContext.Current.CancellationToken);
        var grant = await db.CreditLedgerEntries.SingleAsync(
            item => item.UserId == user.Id,
            TestContext.Current.CancellationToken);

        Assert.Null(user.PasswordHash);
        Assert.NotNull(user.EmailConfirmedAt);
        Assert.Equal(user.Id, login.UserId);
        Assert.Equal(CreditEntryKind.Grant, grant.Kind);
        Assert.Equal(CreditPolicy.NewAccountGrant, grant.Delta);
        Assert.Equal(CreditPolicy.NewAccountGrant, user.CreditBalance);
        Assert.False(await db.AuthenticationEmailOutboxMessages.AnyAsync(
            item => item.UserId == user.Id,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            email,
            TestAccountProtection.CreateService().Unprotect(
                new ProtectedEmailAddress(
                    user.EmailCiphertext,
                    user.EmailNonce,
                    user.EmailAuthenticationTag,
                    user.EmailEncryptionKeyId)));

        var ticket = ReadApplicationTicket(
            factory,
            response.Headers.GetValues("Set-Cookie"));
        Assert.Equal(
            user.Id.ToString(),
            ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task ConcurrentCallbacks_WithSameSubject_CreateOneAccountAndOneGrant()
    {
        var subject = $"concurrent-google-{Guid.NewGuid():N}";
        var email = $"concurrent-google-{Guid.NewGuid():N}@example.test";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        using var firstRequest = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email,
            emailVerified: true);
        using var secondRequest = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email,
            emailVerified: true);

        var firstTask = firstClient.SendAsync(
            firstRequest,
            TestContext.Current.CancellationToken);
        var secondTask = secondClient.SendAsync(
            secondRequest,
            TestContext.Current.CancellationToken);
        var responses = await Task.WhenAll(firstTask, secondTask);
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.All(responses, response =>
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/", response.Headers.Location?.OriginalString);
            Assert.Contains(
                response.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        });

        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, await db.ExternalLogins.CountAsync(
            item => item.Provider == ExternalLoginPolicy.GoogleProvider
                && item.ProviderUserId == subject,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.CreditLedgerEntries.CountAsync(
            item => item.UserId == user.Id
                && item.Kind == CreditEntryKind.Grant
                && item.Delta == CreditPolicy.NewAccountGrant,
            TestContext.Current.CancellationToken));
        Assert.Equal(CreditPolicy.NewAccountGrant, user.CreditBalance);
    }

    [Fact]
    public async Task Callback_WithExistingEmail_DoesNotCreateOrAutoLinkAccount()
    {
        var email = $"existing-google-{Guid.NewGuid():N}@example.test";
        var subject = $"existing-email-subject-{Guid.NewGuid():N}";
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(arrangeDb, email);
            user.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email,
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.OriginalString);
        await using var db = _fixture.NewDbContext();
        var resolvedUser = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        Assert.Equal(userId, resolvedUser.Id);
        Assert.False(await db.ExternalLogins.AnyAsync(
            item => item.Provider == ExternalLoginPolicy.GoogleProvider
                && item.ProviderUserId == subject,
            TestContext.Current.CancellationToken));
        Assert.False(await db.CreditLedgerEntries.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
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
