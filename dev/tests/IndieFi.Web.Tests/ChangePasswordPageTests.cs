using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ChangePasswordPageTests
{
    private const string NewPassword = "New-Page-Test-Password-456!";

    private readonly PostgreSqlFixture _fixture;

    public ChangePasswordPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_WithoutLogin_RedirectsToLogin()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/Login?ReturnUrl=%2FAccount%2FPassword",
            response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Get_AfterLogin_ShowsPasswordFormAndNavigationLink()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name=\"CurrentPassword\"", html);
        Assert.Contains("name=\"NewPassword\"", html);
        Assert.Contains("name=\"ConfirmNewPassword\"", html);
        Assert.Contains("href=\"/Account/Password\"", html);
    }

    [Fact]
    public async Task Post_WithMismatchedConfirmation_LeavesPasswordAndSessionUnchanged()
    {
        var (email, currentPassword) =
            await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var original = await ReadPasswordStateAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, currentPassword);

        using var response = await PostChangeAsync(
            client,
            currentPassword,
            NewPassword,
            "Different-Page-Test-Password-789!");
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        var unchanged = await ReadPasswordStateAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("새 비밀번호가 서로 일치하지 않습니다.", html);
        Assert.DoesNotContain(currentPassword, html);
        Assert.DoesNotContain(NewPassword, html);
        Assert.DoesNotContain("Different-Page-Test-Password-789!", html);
        Assert.Equal(original.PasswordHash, unchanged.PasswordHash);
        Assert.Equal(original.SessionVersion, unchanged.SessionVersion);
    }

    [Fact]
    public async Task Post_WithWrongCurrentPassword_LeavesAccountAndSessionUnchanged()
    {
        const string wrongPassword = "Wrong-Page-Test-Password-789!";
        var (email, currentPassword) =
            await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var original = await ReadPasswordStateAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, currentPassword);

        using var response = await PostChangeAsync(
            client,
            wrongPassword,
            NewPassword,
            NewPassword);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        var unchanged = await ReadPasswordStateAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("현재 비밀번호가 올바르지 않습니다.", html);
        Assert.DoesNotContain(wrongPassword, html);
        Assert.DoesNotContain(NewPassword, html);
        Assert.Equal(original.PasswordHash, unchanged.PasswordHash);
        Assert.Equal(original.SessionVersion, unchanged.SessionVersion);

        using var protectedPageResponse = await client.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, protectedPageResponse.StatusCode);
    }

    [Fact]
    public async Task Post_WithShortNewPassword_LeavesAccountAndSessionUnchanged()
    {
        const string shortPassword = "short";
        var (email, currentPassword) =
            await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var original = await ReadPasswordStateAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, currentPassword);

        using var response = await PostChangeAsync(
            client,
            currentPassword,
            shortPassword,
            shortPassword);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        var unchanged = await ReadPasswordStateAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("새 비밀번호는 8자 이상이어야 합니다.", html);
        Assert.DoesNotContain(currentPassword, html);
        Assert.DoesNotContain(shortPassword, html);
        Assert.Equal(original.PasswordHash, unchanged.PasswordHash);
        Assert.Equal(original.SessionVersion, unchanged.SessionVersion);
    }

    [Fact]
    public async Task Post_WithValidPasswords_SignsOutAndRedirectsToLoginWithMessage()
    {
        var (email, currentPassword) =
            await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, currentPassword);

        using var response = await PostChangeAsync(
            client,
            currentPassword,
            NewPassword,
            NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.OriginalString);
        AssertAuthenticationCookieDeleted(factory, response);

        using var protectedPageResponse = await client.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, protectedPageResponse.StatusCode);

        using var loginResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = WebUtility.HtmlDecode(
            await loginResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        Assert.Contains(
            "비밀번호가 변경되었습니다. 새 비밀번호로 다시 로그인해 주세요.",
            loginHtml);

        using var oldPasswordClient = factory.CreateClient();
        using var oldPasswordResponse = await PostLoginAsync(
            oldPasswordClient,
            email,
            currentPassword);
        var oldPasswordHtml = WebUtility.HtmlDecode(
            await oldPasswordResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, oldPasswordResponse.StatusCode);
        Assert.Contains("이메일 또는 비밀번호가 올바르지 않습니다.", oldPasswordHtml);

        using var newPasswordClient = factory.CreateClient();
        using var newPasswordResponse = await PostLoginAsync(
            newPasswordClient,
            email,
            NewPassword);
        Assert.Equal(HttpStatusCode.Redirect, newPasswordResponse.StatusCode);
    }

    [Fact]
    public async Task Post_AfterTwoClientsLoggedIn_InvalidatesTheOtherClientCookie()
    {
        var (email, currentPassword) =
            await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var original = await ReadPasswordStateAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var changingClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(changingClient, email, currentPassword);
        await PageTestSupport.LoginAsync(otherClient, email, currentPassword);

        using var changeResponse = await PostChangeAsync(
            changingClient,
            currentPassword,
            NewPassword,
            NewPassword);
        Assert.Equal(HttpStatusCode.Redirect, changeResponse.StatusCode);

        using var staleSessionResponse = await otherClient.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var staleSessionHtml = await staleSessionResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var changed = await ReadPasswordStateAsync(email);

        Assert.Equal(HttpStatusCode.OK, staleSessionResponse.StatusCode);
        Assert.DoesNotContain(
            UserDisplayIdentifier.Format(original.UserId),
            staleSessionHtml);
        AssertAuthenticationCookieDeleted(factory, staleSessionResponse);
        Assert.Equal(original.SessionVersion + 1, changed.SessionVersion);
    }

    [Fact]
    public async Task Post_WithoutAntiforgeryToken_RejectsRequestAndLeavesAccountUnchanged()
    {
        var (email, currentPassword) =
            await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var original = await ReadPasswordStateAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, currentPassword);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = currentPassword,
            ["NewPassword"] = NewPassword,
            ["ConfirmNewPassword"] = NewPassword,
        });

        using var response = await client.PostAsync(
            "/Account/Password",
            form,
            TestContext.Current.CancellationToken);
        var unchanged = await ReadPasswordStateAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(original.PasswordHash, unchanged.PasswordHash);
        Assert.Equal(original.SessionVersion, unchanged.SessionVersion);

        using var protectedPageResponse = await client.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, protectedPageResponse.StatusCode);
    }

    private async Task<HttpResponseMessage> PostChangeAsync(
        HttpClient client,
        string currentPassword,
        string newPassword,
        string confirmNewPassword)
    {
        using var pageResponse = await client.GetAsync(
            "/Account/Password",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CurrentPassword"] = currentPassword,
            ["NewPassword"] = newPassword,
            ["ConfirmNewPassword"] = confirmNewPassword,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        return await client.PostAsync(
            "/Account/Password",
            form,
            TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client,
        string email,
        string password)
    {
        using var pageResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        return await client.PostAsync(
            "/Login",
            form,
            TestContext.Current.CancellationToken);
    }

    private static void AssertAuthenticationCookieDeleted(
        WebAppFactory factory,
        HttpResponseMessage response)
    {
        var cookieOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieName = Assert.IsType<string>(cookieOptions.Cookie.Name);

        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{cookieName}=;", StringComparison.Ordinal));
    }

    private async Task<PasswordState> ReadPasswordStateAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        return new PasswordState(user.Id, user.PasswordHash!, user.SessionVersion);
    }

    private sealed record PasswordState(long UserId, string PasswordHash, long SessionVersion);
}
