using System.Net;
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

    private async Task<PasswordState> ReadPasswordStateAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        return new PasswordState(user.PasswordHash!, user.SessionVersion);
    }

    private sealed record PasswordState(string PasswordHash, long SessionVersion);
}
