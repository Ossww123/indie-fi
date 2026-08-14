using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class SessionCookieAuthenticationTests
{
    private readonly PostgreSqlFixture _fixture;

    public SessionCookieAuthenticationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_AfterSessionVersionChanges_RejectsAndDeletesTheOldCookie()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = (await TestAccountProtection.FindUserAsync(
                db,
                email,
                TestContext.Current.CancellationToken)).Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using (var validResponse = await client.GetAsync(
                   "/",
                   TestContext.Current.CancellationToken))
        {
            var validHtml = await validResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
            Assert.Contains(UserDisplayIdentifier.Format(userId), validHtml);
        }

        await using (var ownerDb = _fixture.NewOwnerDbContext())
        {
            var updatedRows = await ownerDb.Users
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        user => user.SessionVersion,
                        user => user.SessionVersion + 1),
                    TestContext.Current.CancellationToken);
            Assert.Equal(1, updatedRows);
        }

        using var rejectedResponse = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var rejectedHtml = await rejectedResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, rejectedResponse.StatusCode);
        Assert.DoesNotContain(UserDisplayIdentifier.Format(userId), rejectedHtml);

        var cookieOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var cookieName = Assert.IsType<string>(cookieOptions.Cookie.Name);
        Assert.Contains(
            rejectedResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{cookieName}=;", StringComparison.Ordinal));
    }
}
