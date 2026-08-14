using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GoogleLoginUiTests
{
    private readonly PostgreSqlFixture _fixture;

    public GoogleLoginUiTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetLogin_WithGoogleConfigured_ShowsProtectedPostEntryAndLocalReturnUrl()
    {
        const string returnUrl = "/KeyDrops/Detail/987";
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync(
            $"/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Google로 계속하기", html);
        Assert.Contains("action=\"/GoogleLogin?handler=Challenge\"", html);
        Assert.Contains("method=\"post\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"name=\"returnUrl\" value=\"{returnUrl}\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.DoesNotContain("google-test-client-id", html);
        Assert.DoesNotContain("google-test-client-secret", html);
    }

    [Fact]
    public async Task DevelopmentWithoutGoogleCredentials_HidesEntryAndChallengeFallsBackToEmailLogin()
    {
        await using var factory = new WebAppFactory(
            _fixture.ConnectionString,
            configureGoogle: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var loginResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(loginHtml),
        });

        using var challengeResponse = await client.PostAsync(
            "/GoogleLogin?handler=Challenge",
            form,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Google로 계속하기", loginHtml);
        Assert.Equal(HttpStatusCode.Redirect, challengeResponse.StatusCode);
        Assert.Equal("/Login", challengeResponse.Headers.Location?.OriginalString);
        Assert.DoesNotContain(
            challengeResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                ".AspNetCore.Correlation.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PostGoogleChallenge_WithoutAntiforgeryToken_IsRejectedBeforeRedirect()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var form = new FormUrlEncodedContent([]);

        using var response = await client.PostAsync(
            "/GoogleLogin?handler=Challenge",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task EmailLogin_WhenGoogleBackchannelWouldFail_DoesNotCallGoogle()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var failingHandler = new FailingBackchannelHandler();
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<GoogleOptions>(
                    ExternalLoginPolicy.GoogleProvider,
                    options => options.BackchannelHttpHandler = failingHandler)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        await PageTestSupport.LoginAsync(client, email, password);

        Assert.Equal(0, failingHandler.CallCount);
    }

    private sealed class FailingBackchannelHandler : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new HttpRequestException("The simulated Google backchannel is unavailable.");
        }
    }
}
