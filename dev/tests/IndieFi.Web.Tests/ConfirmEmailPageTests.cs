using System.Net;
using System.Security.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ConfirmEmailPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public ConfirmEmailPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_WithValidToken_ConfirmsAccountThenRemovesTokenFromAddress()
    {
        var issued = await CreateIssuedTokenAsync();

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var consumeResponse = await client.GetAsync(
            $"/ConfirmEmail?token={Uri.EscapeDataString(issued.Token)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, consumeResponse.StatusCode);
        Assert.Equal("/ConfirmEmail", consumeResponse.Headers.Location?.OriginalString);
        Assert.DoesNotContain(
            issued.Token,
            consumeResponse.Headers.Location?.OriginalString ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal("no-referrer", consumeResponse.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", consumeResponse.Headers.CacheControl?.ToString());

        using var resultResponse = await client.GetAsync(
            consumeResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var html = await resultResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Contains("이메일 확인이 완료되었습니다", html);
        Assert.DoesNotContain(issued.Token, html, StringComparison.Ordinal);

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            item => item.Id == issued.UserId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(user.EmailConfirmedAt);
    }

    [Fact]
    public async Task Get_WithUnknownToken_ShowsOneGenericInvalidMessageWithoutEchoingToken()
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(
            EmailConfirmationTokenService.TokenEntropySize));

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var consumeResponse = await client.GetAsync(
            $"/ConfirmEmail?token={Uri.EscapeDataString(token)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, consumeResponse.StatusCode);
        Assert.Equal("/ConfirmEmail", consumeResponse.Headers.Location?.OriginalString);

        using var resultResponse = await client.GetAsync(
            consumeResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var html = await resultResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resultResponse.StatusCode);
        Assert.Contains("유효하지 않거나 만료된 확인 링크입니다", html);
        Assert.DoesNotContain(token, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_WithoutToken_ShowsNeutralInstructionsWithoutChangingAnAccount()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/ConfirmEmail",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("확인 메일의 링크를 열어 주세요", html);
    }

    private async Task<(long UserId, string Token)> CreateIssuedTokenAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            $"confirm-page-{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var issue = await new EmailConfirmationTokenService(
                db,
                new FixedTimeProvider(now))
            .IssueAsync(user.Id, TestContext.Current.CancellationToken);

        Assert.Equal(EmailConfirmationTokenIssueOutcome.Issued, issue.Outcome);
        return (user.Id, issue.Token!);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
