using System.Net;
using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ResetPasswordPageTests
{
    private const string CurrentPassword = "Current-Page-Password-123!";
    private const string NewPassword = "New-Page-Password-456!";

    private readonly PostgreSqlFixture _fixture;

    public ResetPasswordPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_WithToken_DoesNotConsumeItAndShowsPasswordForm()
    {
        var issued = await CreateIssuedTokenAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/ResetPassword?token={Uri.EscapeDataString(issued.Token)}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name=\"NewPassword\"", html);
        Assert.Contains("name=\"ConfirmNewPassword\"", html);
        Assert.Contains("name=\"Token\"", html);
        Assert.Contains(issued.Token, html, StringComparison.Ordinal);
        Assert.Contains("action=\"/ResetPassword\"", html);
        Assert.DoesNotContain("action=\"/ResetPassword?", html, StringComparison.Ordinal);
        AssertPrivateHeaders(response);

        await AssertTokenUnspentAsync(issued.UserId);
    }

    [Fact]
    public async Task Post_WithValidToken_ResetsPasswordThenRemovesTokenFromResultAddress()
    {
        var issued = await CreateIssuedTokenAsync(sessionVersion: 6);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var resetResponse = await SubmitAsync(
            client,
            issued.Token,
            NewPassword,
            NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, resetResponse.StatusCode);
        Assert.Equal("/ResetPassword", resetResponse.Headers.Location?.OriginalString);
        Assert.DoesNotContain(
            issued.Token,
            resetResponse.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);
        AssertPrivateHeaders(resetResponse);

        using var resultResponse = await client.GetAsync(
            resetResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var resultHtml = await resultResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Contains("비밀번호가 재설정되었습니다", resultHtml);
        Assert.Contains("/Login", resultHtml);
        Assert.DoesNotContain(issued.Token, resultHtml, StringComparison.Ordinal);
        AssertPrivateHeaders(resultResponse);

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            account => account.Id == issued.UserId,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.PasswordResetTokens.SingleAsync(
            item => item.UserId == issued.UserId,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, user.SessionVersion);
        AssertPasswordFails(user, CurrentPassword);
        AssertPasswordMatches(user, NewPassword);
        Assert.NotNull(token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task Post_WithUnknownToken_ShowsGenericResultWithoutEchoingToken()
    {
        var token = CreatePlaintextToken();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var resetResponse = await SubmitAsync(
            client,
            token,
            NewPassword,
            NewPassword);

        Assert.Equal(HttpStatusCode.Redirect, resetResponse.StatusCode);
        Assert.Equal("/ResetPassword", resetResponse.Headers.Location?.OriginalString);

        using var resultResponse = await client.GetAsync(
            resetResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var html = await resultResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Contains("유효하지 않거나 만료된 비밀번호 재설정 링크입니다", html);
        Assert.DoesNotContain(token, html, StringComparison.Ordinal);
        AssertPrivateHeaders(resultResponse);
    }

    [Fact]
    public async Task Post_WithMismatchedPasswords_ShowsFieldErrorAndLeavesTokenUnspent()
    {
        var issued = await CreateIssuedTokenAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await SubmitAsync(
            client,
            issued.Token,
            NewPassword,
            "Different-Page-Password-789!");
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "새 비밀번호가 서로 일치하지 않습니다",
            WebUtility.HtmlDecode(html));
        Assert.Contains(issued.Token, html, StringComparison.Ordinal);
        AssertPrivateHeaders(response);
        await AssertTokenUnspentAsync(issued.UserId);
    }

    [Fact]
    public async Task Post_WithoutAntiforgeryToken_IsRejectedAndLeavesTokenUnspent()
    {
        var issued = await CreateIssuedTokenAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Token"] = issued.Token,
            ["NewPassword"] = NewPassword,
            ["ConfirmNewPassword"] = NewPassword,
        });
        using var response = await client.PostAsync(
            "/ResetPassword",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertTokenUnspentAsync(issued.UserId);
    }

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string token,
        string newPassword,
        string confirmNewPassword)
    {
        using var pageResponse = await client.GetAsync(
            $"/ResetPassword?token={Uri.EscapeDataString(token)}",
            TestContext.Current.CancellationToken);
        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Token"] = token,
            ["NewPassword"] = newPassword,
            ["ConfirmNewPassword"] = confirmNewPassword,
            ["__RequestVerificationToken"] = PageTestSupport.ExtractAntiforgeryToken(html),
        });
        return await client.PostAsync(
            "/ResetPassword",
            form,
            TestContext.Current.CancellationToken);
    }

    private async Task<(long UserId, string Token)> CreateIssuedTokenAsync(
        long sessionVersion = 1)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"reset-page-{Guid.NewGuid():N}@example.test",
            passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(
            user,
            CurrentPassword);
        user.EmailConfirmedAt = now.AddDays(-1);
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var token = CreatePlaintextToken();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(30),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (user.Id, token);
    }

    private async Task AssertTokenUnspentAsync(long userId)
    {
        await using var db = _fixture.NewDbContext();
        var token = await db.PasswordResetTokens.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Null(token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    private static string CreatePlaintextToken() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(
            PasswordResetTokenService.TokenEntropySize));

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

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
}
