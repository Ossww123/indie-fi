using System.Net;
using System.Text.RegularExpressions;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace IndieFi.Web.Tests;

internal static class PageTestSupport
{
    public static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            """<input\b(?=[^>]*\btype="hidden")(?=[^>]*\bname="__RequestVerificationToken")[^>]*\bvalue="([^"]+)"[^>]*>""",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            throw new InvalidOperationException(
                "The response HTML did not contain an antiforgery token.");
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static async Task<(string Email, string Password)> CreateUserWithKnownPasswordAsync(
        PostgreSqlFixture fixture)
    {
        const string password = "Known-Test-Password-123!";
        var email = $"{Guid.NewGuid():N}@example.test";

        await using var db = fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email, passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (email, password);
    }

    public static async Task LoginAsync(HttpClient client, string email, string password)
    {
        using var loginPageResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginPageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = ExtractAntiforgeryToken(loginHtml);

        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        using var loginResponse = await client.PostAsync(
            "/Login",
            loginForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
    }
}
