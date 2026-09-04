using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
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
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        var entries = await db.CreditLedgerEntries
            .Where(entry => entry.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Signup saves the ledger entry and balance together; checking only one side
        // would let the test pass if the other side were omitted.
        Assert.Equal(3, user.CreditBalance);
        var entry = Assert.Single(entries);
        Assert.Equal(CreditEntryKind.Grant, entry.Kind);
        Assert.Equal(3, entry.Delta);
    }

    [Fact]
    public async Task AfterSignup_EveryUsersCreditBalance_MatchesTheirLedgerSum()
    {
        // Signing up here is what makes this test self-sufficient. Relying on other
        // tests to produce credit-carrying users made it pass for the wrong reason:
        // xUnit runs this class roughly in name order, so it ran before they existed
        // and reported success against an empty set.
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await SignupAsync(client, $"{Guid.NewGuid():N}@example.test", "Known-Test-Password-123!");

        await using var db = _fixture.NewDbContext();

        // The sweep still covers every user, not just the one above, so a break in any
        // other path shows up here too whenever that path ran first.
        var mismatches = await db.Users
            .Select(user => new
            {
                UserId = user.Id,
                Balance = user.CreditBalance,
                LedgerSum = db.CreditLedgerEntries
                    .Where(entry => entry.UserId == user.Id)
                    .Sum(entry => (int?)entry.Delta) ?? 0,
            })
            .Where(values => values.Balance != values.LedgerSum)
            .OrderBy(values => values.UserId)
            .ToListAsync(TestContext.Current.CancellationToken);

        if (mismatches.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                mismatches.Select(values =>
                    $"User {values.UserId}: balance={values.Balance}, ledger sum={values.LedgerSum}"));

            Assert.Fail($"Credit balance mismatches:{Environment.NewLine}{details}");
        }
    }

    [Fact]
    public async Task Get_CreditsPage_ShowsBalanceAndGrantEntry()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string password = "Known-Test-Password-123!";

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await SignupAsync(client, email, password);
        await ConfirmEmailForPageSetupAsync(email);
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
        Assert.Contains("<strong>3</strong> 크레딧", decodedHtml);
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

    private async Task ConfirmEmailForPageSetupAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        user.EmailConfirmedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
