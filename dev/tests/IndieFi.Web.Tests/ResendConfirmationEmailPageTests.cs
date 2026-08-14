using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ResendConfirmationEmailPageTests
{
    private const string GenericMessage =
        "입력한 이메일의 미확인 계정이 재발송 제한 범위 안에 있으면 확인 메일을 보냈습니다.";

    private readonly PostgreSqlFixture _fixture;

    public ResendConfirmationEmailPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_ForExistingAndUnknownEmail_ShowsSameGenericResult()
    {
        var existingEmail = $"page-resend-{Guid.NewGuid():N}@example.test";
        var unknownEmail = $"page-unknown-{Guid.NewGuid():N}@example.test";
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(db, existingEmail);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var existingClient = factory.CreateClient();
        using var unknownClient = factory.CreateClient();

        var existingResult = await PostAsync(existingClient, existingEmail);
        var unknownResult = await PostAsync(unknownClient, unknownEmail);

        Assert.Contains(GenericMessage, WebUtility.HtmlDecode(existingResult));
        Assert.Contains(GenericMessage, WebUtility.HtmlDecode(unknownResult));

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Single(await verificationDb.EmailConfirmationTokens
            .Where(token => token.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verificationDb.AuthenticationEmailOutboxMessages
            .Where(message => message.UserId == userId)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_ShowsEmailFormAndNavigationLinksReachIt()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var pageResponse = await client.GetAsync(
            "/ResendConfirmationEmail",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var loginResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var confirmationResponse = await client.GetAsync(
            "/ConfirmEmail",
            TestContext.Current.CancellationToken);
        var confirmationHtml = await confirmationResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Contains("no-store", pageResponse.Headers.CacheControl?.ToString());
        Assert.Equal(
            "no-referrer",
            pageResponse.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("확인 메일 다시 받기", pageHtml);
        Assert.Contains("name=\"Email\"", pageHtml);
        Assert.Contains("/ResendConfirmationEmail", loginHtml);
        Assert.Contains("/ResendConfirmationEmail", confirmationHtml);
    }

    private static async Task<string> PostAsync(HttpClient client, string email)
    {
        using var pageResponse = await client.GetAsync(
            "/ResendConfirmationEmail",
            TestContext.Current.CancellationToken);
        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(html);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });

        using var postResponse = await client.PostAsync(
            "/ResendConfirmationEmail",
            form,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, postResponse.StatusCode);
        Assert.Equal(
            "/ResendConfirmationEmail",
            postResponse.Headers.Location?.OriginalString);

        using var resultResponse = await client.GetAsync(
            postResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        return await resultResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
    }
}
