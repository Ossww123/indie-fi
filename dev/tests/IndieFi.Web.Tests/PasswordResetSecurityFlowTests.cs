using System.Net;
using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PasswordResetSecurityFlowTests
{
    private const string CurrentPassword = "Current-Reset-Flow-Password-123!";
    private const string NewPassword = "New-Reset-Flow-Password-456!";

    private readonly PostgreSqlFixture _fixture;

    public PasswordResetSecurityFlowTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Reset_AfterTwoClientsLoggedIn_InvalidatesBothExistingSessions()
    {
        var account = await CreateAccountWithResetTokenAsync(sessionVersion: 5);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var resettingClient = factory.CreateClient();
        using var otherClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(
            resettingClient,
            account.Email,
            CurrentPassword);
        await PageTestSupport.LoginAsync(
            otherClient,
            account.Email,
            CurrentPassword);

        await AssertAuthenticatedAsync(resettingClient, account.UserId);
        await AssertAuthenticatedAsync(otherClient, account.UserId);

        using var resetResponse = await SubmitResetAsync(
            resettingClient,
            account.Token,
            NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, resetResponse.StatusCode);
        Assert.Equal("/ResetPassword", resetResponse.Headers.Location?.OriginalString);
        AssertAuthenticationCookieDeleted(factory, resetResponse);

        await AssertAnonymousAsync(resettingClient, account.UserId);
        await AssertRejectedAndDeletedAsync(factory, otherClient, account.UserId);

        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            item => item.Id == account.UserId,
            TestContext.Current.CancellationToken);
        Assert.Equal(6, user.SessionVersion);
    }

    [Fact]
    public async Task Reset_RejectsOldPasswordAcceptsNewPasswordAndPreventsTokenReuse()
    {
        var account = await CreateAccountWithResetTokenAsync(sessionVersion: 8);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var resetClient = factory.CreateClient();
        using var resetResponse = await SubmitResetAsync(
            resetClient,
            account.Token,
            NewPassword);
        Assert.Equal(HttpStatusCode.Redirect, resetResponse.StatusCode);

        using var oldPasswordClient = factory.CreateClient();
        using var oldPasswordResponse = await PostLoginAsync(
            oldPasswordClient,
            account.Email,
            CurrentPassword);
        var oldPasswordHtml = WebUtility.HtmlDecode(
            await oldPasswordResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, oldPasswordResponse.StatusCode);
        Assert.Contains(
            "이메일 또는 비밀번호가 올바르지 않습니다.",
            oldPasswordHtml);

        using var newPasswordClient = factory.CreateClient();
        using var newPasswordResponse = await PostLoginAsync(
            newPasswordClient,
            account.Email,
            NewPassword);
        Assert.Equal(HttpStatusCode.Redirect, newPasswordResponse.StatusCode);
        await AssertAuthenticatedAsync(newPasswordClient, account.UserId);

        using var replayClient = factory.CreateClient();
        using var replayResponse = await SubmitResetAsync(
            replayClient,
            account.Token,
            "Another-Reset-Flow-Password-789!");

        Assert.Equal(HttpStatusCode.Redirect, replayResponse.StatusCode);
        Assert.Equal("/ResetPassword", replayResponse.Headers.Location?.OriginalString);
        Assert.DoesNotContain(
            account.Token,
            replayResponse.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);

        using var replayResultResponse = await replayClient.GetAsync(
            replayResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var replayHtml = await replayResultResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, replayResultResponse.StatusCode);
        Assert.Contains("유효하지 않거나 만료된 비밀번호 재설정 링크입니다", replayHtml);
        Assert.DoesNotContain(account.Token, replayHtml, StringComparison.Ordinal);
        AssertPrivateHeaders(replayResultResponse);

        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            item => item.Id == account.UserId,
            TestContext.Current.CancellationToken);
        var token = await db.PasswordResetTokens.SingleAsync(
            item => item.UserId == account.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(9, user.SessionVersion);
        AssertPasswordMatches(user, NewPassword);
        AssertPasswordFails(user, CurrentPassword);
        Assert.NotNull(token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    private static async Task<HttpResponseMessage> SubmitResetAsync(
        HttpClient client,
        string token,
        string newPassword)
    {
        using var pageResponse = await client.GetAsync(
            $"/ResetPassword?token={Uri.EscapeDataString(token)}",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Token"] = token,
            ["NewPassword"] = newPassword,
            ["ConfirmNewPassword"] = newPassword,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        return await client.PostAsync(
            "/ResetPassword",
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

    private async Task<AccountWithResetToken> CreateAccountWithResetTokenAsync(
        long sessionVersion)
    {
        var now = DateTimeOffset.UtcNow;
        var email = $"reset-flow-{Guid.NewGuid():N}@example.test";
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email, passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(
            user,
            CurrentPassword);
        user.EmailConfirmedAt = now.AddDays(-1);
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(
            PasswordResetTokenService.TokenEntropySize));
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(30),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new AccountWithResetToken(user.Id, email, token);
    }

    private static async Task AssertAuthenticatedAsync(HttpClient client, long userId)
    {
        using var response = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(UserDisplayIdentifier.Format(userId), html);
    }

    private static async Task AssertAnonymousAsync(HttpClient client, long userId)
    {
        using var response = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(UserDisplayIdentifier.Format(userId), html);
    }

    private static async Task AssertRejectedAndDeletedAsync(
        WebAppFactory factory,
        HttpClient client,
        long userId)
    {
        using var response = await client.GetAsync(
            "/",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(UserDisplayIdentifier.Format(userId), html);
        AssertAuthenticationCookieDeleted(factory, response);
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

    private static void AssertPrivateHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            "no-referrer",
            response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    private static void AssertPasswordMatches(User user, string password)
    {
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(
                user,
                user.PasswordHash!,
                password));
    }

    private static void AssertPasswordFails(User user, string password)
    {
        Assert.Equal(
            PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(
                user,
                user.PasswordHash!,
                password));
    }

    private sealed record AccountWithResetToken(
        long UserId,
        string Email,
        string Token);
}
