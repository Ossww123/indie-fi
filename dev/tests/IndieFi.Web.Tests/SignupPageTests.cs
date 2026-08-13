using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class SignupPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public SignupPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Signup_ShowsUserIdentifierWithoutEmail()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var signupPageResponse = await client.GetAsync(
            "/Signup",
            TestContext.Current.CancellationToken);
        var signupHtml = await signupPageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(signupHtml);

        using var signupForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var signupResponse = await client.PostAsync(
            "/Signup",
            signupForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, signupResponse.StatusCode);
        Assert.Equal("/", signupResponse.Headers.Location?.OriginalString);

        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = await db.Users
                .Where(user => user.Email == email)
                .Select(user => user.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        using var homeResponse = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var homeHtml = await homeResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        Assert.Contains(UserDisplayIdentifier.Format(userId), homeHtml);
        Assert.DoesNotContain(email, homeHtml);
    }
}
