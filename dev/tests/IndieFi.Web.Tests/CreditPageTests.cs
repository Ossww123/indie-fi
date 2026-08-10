using System.Net;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class CreditPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public CreditPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Post_Signup_GrantsThreeCreditsInASingleEntry()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await SignupAsync(client, email, password);

        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            user => user.Email == email,
            TestContext.Current.CancellationToken);
        var entries = await db.CreditLedgerEntries
            .Where(entry => entry.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The account and grant must commit together because splitting SaveChanges
        // can leave an account without credits; the complete ledger guards that invariant.
        var entry = Assert.Single(entries);
        Assert.Equal(CreditEntryKind.Grant, entry.Kind);
        Assert.Equal(3, entry.Delta);
    }

    [Fact]
    public async Task Get_CreditsPage_ShowsBalanceAndGrantEntry()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await SignupAsync(client, email, password);
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Credits",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Razor can write non-Latin expression values as numeric character references,
        // so decoding consistently compares the text that the reader sees.
        var decodedHtml = WebUtility.HtmlDecode(html);
        Assert.Contains("현재 3 크레딧", decodedHtml);
        Assert.Contains("+3", decodedHtml);
        Assert.Contains("가입 축하 지급", decodedHtml);
    }

    private static async Task SignupAsync(HttpClient client, string email, string password)
    {
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
    }
}
