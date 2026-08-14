using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class EmailSignupFlowTests
{
    private readonly PostgreSqlFixture _fixture;

    public EmailSignupFlowTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Signup_ConfirmationLink_ThenLogin_CompletesEmailAccountFlow()
    {
        var email = $"flow-{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PostSignupAsync(client, email, password);

        long userId;
        string plaintextToken;
        await using (var db = _fixture.NewDbContext())
        {
            var user = await TestAccountProtection.FindUserAsync(
                db,
                email,
                TestContext.Current.CancellationToken);
            var message = await db.AuthenticationEmailOutboxMessages.SingleAsync(
                item => item.UserId == user.Id,
                TestContext.Current.CancellationToken);
            userId = user.Id;
            plaintextToken = TestAccountProtection.CreateMessageProtectionService().Unprotect(
                user.Id,
                message.Kind,
                new ProtectedAuthenticationEmailPayload(
                    message.PayloadCiphertext,
                    message.PayloadNonce,
                    message.PayloadAuthenticationTag,
                    message.PayloadKeyId));
        }

        // Opening the mailed link shows the button; pressing it is what confirms.
        string confirmationPageHtml;
        using (var linkResponse = await client.GetAsync(
                   $"/ConfirmEmail?token={Uri.EscapeDataString(plaintextToken)}",
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);
            confirmationPageHtml = await linkResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
        }

        using (var confirmationForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Token"] = plaintextToken,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(confirmationPageHtml),
        }))
        using (var confirmationResponse = await client.PostAsync(
                   "/ConfirmEmail",
                   confirmationForm,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, confirmationResponse.StatusCode);
            Assert.Equal(
                "/ConfirmEmail",
                confirmationResponse.Headers.Location?.OriginalString);
        }

        await PageTestSupport.LoginAsync(client, email, password);

        using var homeResponse = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var homeHtml = await homeResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, homeResponse.StatusCode);
        Assert.Contains(UserDisplayIdentifier.Format(userId), homeHtml);
    }

    private static async Task PostSignupAsync(
        HttpClient client,
        string email,
        string password)
    {
        using var pageResponse = await client.GetAsync(
            "/Signup",
            TestContext.Current.CancellationToken);
        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(html);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var response = await client.PostAsync(
            "/Signup",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }
}
