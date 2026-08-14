using System.Net;
using System.Security.Claims;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
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
            emailVerified: true,
            includeProviderTokens: true);

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
        Assert.Empty(applicationTicket.Properties.GetTokens());
    }

    [Theory]
    [InlineData(AccountStatus.TemporarilySuspended)]
    [InlineData(AccountStatus.IndefinitelySuspended)]
    public async Task Callback_WithSuspendedLinkedAccount_DoesNotIssueApplicationSession(
        AccountStatus accountStatus)
    {
        var subject = $"suspended-linked-google-{Guid.NewGuid():N}";
        var suspendedUntil = accountStatus == AccountStatus.TemporarilySuspended
            ? DateTimeOffset.UtcNow.AddHours(1)
            : (DateTimeOffset?)null;
        var userId = await CreateLinkedUserAsync(
            subject,
            sessionVersion: 11,
            accountStatus: accountStatus,
            suspendedUntil: suspendedUntil);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email: "suspended-linked@example.test",
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));

        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(accountStatus, user.AccountStatus);
        Assert.Equal(11, user.SessionVersion);
    }

    [Fact]
    public async Task Callback_AfterLinkedAccountTemporarySuspensionExpires_ReactivatesAndIssuesNewSession()
    {
        var subject = $"expired-linked-google-{Guid.NewGuid():N}";
        var userId = await CreateLinkedUserAsync(
            subject,
            sessionVersion: 11,
            accountStatus: AccountStatus.TemporarilySuspended,
            suspendedUntil: DateTimeOffset.UtcNow.AddMinutes(-1));
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email: "expired-linked@example.test",
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var ticket = ReadApplicationTicket(
            factory,
            response.Headers.GetValues("Set-Cookie"));
        Assert.Equal(
            "12",
            ticket.Principal.FindFirstValue(AuthenticationClaimTypes.SessionVersion));

        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(AccountStatus.Active, user.AccountStatus);
        Assert.Null(user.SuspendedUntil);
        Assert.Equal(12, user.SessionVersion);
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
        Assert.Equal("/GoogleLink", response.Headers.Location?.OriginalString);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            Assert.DoesNotContain(
                setCookies,
                value => value.StartsWith(
                    $"{ExternalAuthenticationDefaults.TemporaryCookieName}=",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                setCookies,
                value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        }
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
    public async Task Callback_WithSuspendedPasswordAccount_DoesNotStartGoogleLinkFlow()
    {
        const string password = "Existing-Password-123!";
        var email = $"suspended-google-link-{Guid.NewGuid():N}@example.test";
        var subject = $"suspended-google-link-subject-{Guid.NewGuid():N}";
        var userId = await CreatePasswordUserAsync(
            email,
            password,
            accountStatus: AccountStatus.TemporarilySuspended,
            suspendedUntil: DateTimeOffset.UtcNow.AddHours(1));
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
        Assert.DoesNotContain(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.ExternalLogins.AnyAsync(
            item => item.UserId == userId
                && item.Provider == ExternalLoginPolicy.GoogleProvider,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetGoogleLink_WithValidTemporaryIdentity_ShowsPasswordOnlyAndPrivateHeaders()
    {
        const string password = "Existing-Password-123!";
        var email = $"google-link-get-{Guid.NewGuid():N}@example.test";
        var subject = $"google-link-get-subject-{Guid.NewGuid():N}";
        await CreatePasswordUserAsync(email, password);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateExternalRequest(
            factory,
            HttpMethod.Get,
            "/GoogleLink?returnUrl=%2FAccount%2FPassword",
            subject,
            email,
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("name=\"Password\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.DoesNotContain(email, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(subject, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostGoogleLink_WithCorrectPassword_LinksAndSignsInWithoutNewCredit()
    {
        const string password = "Existing-Password-123!";
        const string returnUrl = "/Account/Password";
        var email = $"google-link-success-{Guid.NewGuid():N}@example.test";
        var subject = $"google-link-success-subject-{Guid.NewGuid():N}";
        var userId = await CreatePasswordUserAsync(
            email,
            password,
            emailConfirmed: false);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        var pageUrl = $"/GoogleLink?returnUrl={Uri.EscapeDataString(returnUrl)}";
        using var getRequest = CreateExternalRequest(
            factory,
            HttpMethod.Get,
            pageUrl,
            subject,
            email,
            emailVerified: true);
        using var getResponse = await client.SendAsync(
            getRequest,
            TestContext.Current.CancellationToken);
        var getHtml = await getResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(getHtml),
        });
        using var postRequest = CreateExternalRequest(
            factory,
            HttpMethod.Post,
            "/GoogleLink",
            subject,
            email,
            emailVerified: true,
            form);

        using var response = await client.SendAsync(
            postRequest,
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
        var ticket = ReadApplicationTicket(factory, setCookies);
        Assert.Equal(
            userId.ToString(),
            ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier));

        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        var login = await db.ExternalLogins.SingleAsync(
            item => item.Provider == ExternalLoginPolicy.GoogleProvider
                && item.ProviderUserId == subject,
            TestContext.Current.CancellationToken);
        Assert.Equal(userId, login.UserId);
        Assert.NotNull(user.EmailConfirmedAt);
        Assert.False(await db.CreditLedgerEntries.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostGoogleLink_WithWrongPassword_LeavesTemporaryIdentityAndAccountUnchanged()
    {
        const string password = "Existing-Password-123!";
        var email = $"google-link-wrong-{Guid.NewGuid():N}@example.test";
        var subject = $"google-link-wrong-subject-{Guid.NewGuid():N}";
        var userId = await CreatePasswordUserAsync(email, password);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var getRequest = CreateExternalRequest(
            factory,
            HttpMethod.Get,
            "/GoogleLink",
            subject,
            email,
            emailVerified: true);
        using var getResponse = await client.SendAsync(
            getRequest,
            TestContext.Current.CancellationToken);
        var getHtml = await getResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Password"] = "Wrong-Password-123!",
            ["ReturnUrl"] = "/",
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(getHtml),
        });
        using var postRequest = CreateExternalRequest(
            factory,
            HttpMethod.Post,
            "/GoogleLink",
            subject,
            email,
            emailVerified: true,
            form);

        using var response = await client.SendAsync(
            postRequest,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("현재 비밀번호가 올바르지 않습니다", html);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            Assert.DoesNotContain(
                setCookies,
                value => value.StartsWith(
                    $"{ExternalAuthenticationDefaults.TemporaryCookieName}=",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                setCookies,
                value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        }
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.ExternalLogins.AnyAsync(
            item => item.UserId == userId
                && item.Provider == ExternalLoginPolicy.GoogleProvider,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostGoogleLink_WithoutAntiforgeryToken_IsRejectedBeforeLinking()
    {
        const string password = "Existing-Password-123!";
        var email = $"google-link-csrf-{Guid.NewGuid():N}@example.test";
        var subject = $"google-link-csrf-subject-{Guid.NewGuid():N}";
        var userId = await CreatePasswordUserAsync(email, password);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Password"] = password,
            ["ReturnUrl"] = "/",
        });
        using var request = CreateExternalRequest(
            factory,
            HttpMethod.Post,
            "/GoogleLink",
            subject,
            email,
            emailVerified: true,
            form);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.ExternalLogins.AnyAsync(
            item => item.UserId == userId
                && item.Provider == ExternalLoginPolicy.GoogleProvider,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Callback_WithPasswordlessEmailCollision_RejectsNewSubject()
    {
        var email = $"google-link-passwordless-{Guid.NewGuid():N}@example.test";
        var existingSubject = $"existing-passwordless-{Guid.NewGuid():N}";
        var newSubject = $"new-passwordless-{Guid.NewGuid():N}";
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await using var transaction = await arrangeDb.Database.BeginTransactionAsync(
                TestContext.Current.CancellationToken);
            var user = TestAccountProtection.AddUser(
                arrangeDb,
                email,
                passwordHash: null);
            user.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.ExternalLogins.Add(new ExternalLogin
            {
                UserId = user.Id,
                Provider = ExternalLoginPolicy.GoogleProvider,
                ProviderUserId = existingSubject,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            newSubject,
            email,
            emailVerified: true);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.OriginalString);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                $"{ExternalAuthenticationDefaults.TemporaryCookieName}=",
                StringComparison.Ordinal));
        await using var db = _fixture.NewDbContext();
        var logins = await db.ExternalLogins
            .Where(item => item.UserId == userId)
            .Select(item => item.ProviderUserId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { existingSubject }, logins);
    }

    [Fact]
    public async Task Callback_WithExpiredTemporaryIdentity_DoesNotCreateOrSignInAccount()
    {
        var email = $"expired-google-{Guid.NewGuid():N}@example.test";
        var subject = $"expired-google-subject-{Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var request = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            subject,
            email,
            emailVerified: true,
            expiresUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.OriginalString);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        Assert.DoesNotContain(
            setCookies,
            value => value.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        Assert.Contains(
            setCookies,
            value => value.StartsWith(
                $"{ExternalAuthenticationDefaults.TemporaryCookieName}=",
                StringComparison.Ordinal));

        await using var db = _fixture.NewDbContext();
        Assert.False(await db.ExternalLogins.AnyAsync(
            item => item.Provider == ExternalLoginPolicy.GoogleProvider
                && item.ProviderUserId == subject,
            TestContext.Current.CancellationToken));
        Assert.False(await EmailLookupExistsAsync(db, email));
    }

    [Fact]
    public async Task ConcurrentCallbacks_WithDifferentSubjectsAndSameEmail_CreateOneRewardedAccount()
    {
        var email = $"competing-google-{Guid.NewGuid():N}@example.test";
        var firstSubject = $"competing-google-first-{Guid.NewGuid():N}";
        var secondSubject = $"competing-google-second-{Guid.NewGuid():N}";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        using var firstRequest = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            firstSubject,
            email,
            emailVerified: true);
        using var secondRequest = CreateCallbackRequest(
            factory,
            "/GoogleLogin?handler=Callback",
            secondSubject,
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

        Assert.Single(
            responses,
            response => response.Headers.Location?.OriginalString == "/"
                && response.Headers.GetValues("Set-Cookie").Any(
                    value => value.StartsWith(
                        ".AspNetCore.Cookies=",
                        StringComparison.Ordinal)));
        Assert.Single(
            responses,
            response => response.Headers.Location?.OriginalString == "/Login");

        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        var login = await db.ExternalLogins.SingleAsync(
            item => item.UserId == user.Id
                && item.Provider == ExternalLoginPolicy.GoogleProvider,
            TestContext.Current.CancellationToken);
        Assert.Contains(login.ProviderUserId, new[] { firstSubject, secondSubject });
        Assert.Equal(1, await db.CreditLedgerEntries.CountAsync(
            item => item.UserId == user.Id
                && item.Kind == CreditEntryKind.Grant
                && item.Delta == CreditPolicy.NewAccountGrant,
            TestContext.Current.CancellationToken));
        Assert.Equal(CreditPolicy.NewAccountGrant, user.CreditBalance);
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
        long sessionVersion = 1,
        AccountStatus accountStatus = AccountStatus.Active,
        DateTimeOffset? suspendedUntil = null)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"linked-google-{Guid.NewGuid():N}@example.test");
        user.EmailConfirmedAt = DateTimeOffset.UtcNow;
        user.SessionVersion = sessionVersion;
        user.AccountStatus = accountStatus;
        user.SuspendedUntil = suspendedUntil;
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

    private async Task<long> CreatePasswordUserAsync(
        string email,
        string password,
        bool emailConfirmed = true,
        AccountStatus accountStatus = AccountStatus.Active,
        DateTimeOffset? suspendedUntil = null)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email, passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>()
            .HashPassword(user, password);
        user.EmailConfirmedAt = emailConfirmed ? DateTimeOffset.UtcNow : null;
        user.AccountStatus = accountStatus;
        user.SuspendedUntil = suspendedUntil;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private static async Task<bool> EmailLookupExistsAsync(
        AppDbContext db,
        string email)
    {
        Assert.True(AccountEmailAddressPolicy.TryCreate(email, out var address, out _));
        foreach (var lookup in TestAccountProtection
                     .CreateService()
                     .CreateReadableLookupValues(address!))
        {
            if (await db.UserEmailLookups.AnyAsync(
                    item => item.KeyId == lookup.KeyId
                        && item.Value.SequenceEqual(lookup.Value),
                    TestContext.Current.CancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static HttpRequestMessage CreateCallbackRequest(
        WebAppFactory factory,
        string requestUri,
        string subject,
        string email,
        bool emailVerified,
        DateTimeOffset? expiresUtc = null,
        bool includeProviderTokens = false)
    {
        return CreateExternalRequest(
            factory,
            HttpMethod.Get,
            requestUri,
            subject,
            email,
            emailVerified,
            expiresUtc: expiresUtc,
            includeProviderTokens: includeProviderTokens);
    }

    private static HttpRequestMessage CreateExternalRequest(
        WebAppFactory factory,
        HttpMethod method,
        string requestUri,
        string subject,
        string email,
        bool emailVerified,
        HttpContent? content = null,
        DateTimeOffset? expiresUtc = null,
        bool includeProviderTokens = false)
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
        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = expiresUtc ?? DateTimeOffset.UtcNow.Add(
                ExternalAuthenticationDefaults.TemporaryLifetime),
        };
        if (includeProviderTokens)
        {
            properties.StoreTokens(
            [
                new AuthenticationToken
                {
                    Name = "access_token",
                    Value = "provider-access-token-must-not-cross-boundary",
                },
                new AuthenticationToken
                {
                    Name = "refresh_token",
                    Value = "provider-refresh-token-must-not-cross-boundary",
                },
            ]);
        }

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                ExternalAuthenticationDefaults.TemporaryScheme)),
            properties,
            ExternalAuthenticationDefaults.TemporaryScheme);
        var protectedTicket = options.TicketDataFormat.Protect(ticket);
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = content,
        };
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
