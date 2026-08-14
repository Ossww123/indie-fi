using System.Net;
using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ForgotPasswordPageTests
{
    private const string GenericMessage =
        "입력한 이메일에 재설정 가능한 계정이 있고 요청 제한 범위 안이면 비밀번호 재설정 메일을 보냈습니다.";

    private readonly PostgreSqlFixture _fixture;

    public ForgotPasswordPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_ShowsFormAndLoginNavigationWithPrivateHeaders()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var pageResponse = await client.GetAsync(
            "/ForgotPassword",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var loginResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        Assert.Contains("no-store", pageResponse.Headers.CacheControl?.ToString());
        Assert.Equal(
            "no-referrer",
            pageResponse.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("비밀번호 찾기", pageHtml);
        Assert.Contains("name=\"Email\"", pageHtml);
        Assert.Contains("/Login", pageHtml);
        Assert.Contains("/ForgotPassword", loginHtml);
        Assert.Contains("비밀번호를 잊으셨나요?", loginHtml);
    }

    [Fact]
    public async Task Post_ForExistingAndUnknownEmail_UsesSamePrgResult()
    {
        var existingEmail = $"forgot-existing-{Guid.NewGuid():N}@example.test";
        var unknownEmail = $"forgot-unknown-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(existingEmail);
        var oldToken = CreatePlaintextToken(61);
        await SeedPendingResetAsync(
            userId,
            oldToken,
            DateTimeOffset.UtcNow.AddMinutes(-2));

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var existingClient = factory.CreateClient();
        using var unknownClient = factory.CreateClient();

        var existingResult = await PostAsync(existingClient, existingEmail);
        var unknownResult = await PostAsync(unknownClient, unknownEmail);

        Assert.Equal("/ForgotPassword", existingResult.Location);
        Assert.Equal(existingResult.Location, unknownResult.Location);
        Assert.Equal(HttpStatusCode.OK, existingResult.ResultStatusCode);
        Assert.Equal(existingResult.ResultStatusCode, unknownResult.ResultStatusCode);
        Assert.Contains(GenericMessage, WebUtility.HtmlDecode(existingResult.Html));
        Assert.Contains(GenericMessage, WebUtility.HtmlDecode(unknownResult.Html));
        Assert.DoesNotContain(existingEmail, existingResult.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(unknownEmail, unknownResult.Html, StringComparison.OrdinalIgnoreCase);
        AssertPrivateHeaders(existingResult);
        AssertPrivateHeaders(unknownResult);

        await using var verificationDb = _fixture.NewDbContext();
        var tokens = await verificationDb.PasswordResetTokens
            .Where(token => token.UserId == userId)
            .OrderBy(token => token.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        var message = await verificationDb.AuthenticationEmailOutboxMessages.SingleAsync(
            item => item.UserId == userId
                && item.Kind == AuthenticationEmailMessageKind.PasswordReset,
            TestContext.Current.CancellationToken);
        var newToken = Unprotect(message);

        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].RevokedAt);
        Assert.Null(tokens[1].RevokedAt);
        Assert.Equal(HashToken(oldToken), tokens[0].TokenHash);
        Assert.Equal(HashToken(newToken), tokens[1].TokenHash);
        Assert.Equal(tokens[1].ExpiresAt, message.ExpiresAt);
    }

    [Fact]
    public async Task Post_ForIneligibleAndLimitedAccounts_ShowsTheSameGenericResult()
    {
        var unconfirmedEmail = $"forgot-unconfirmed-{Guid.NewGuid():N}@example.test";
        var passwordlessEmail = $"forgot-passwordless-{Guid.NewGuid():N}@example.test";
        var limitedEmail = $"forgot-limited-{Guid.NewGuid():N}@example.test";
        var unconfirmedUserId = await CreateUserAsync(unconfirmedEmail, confirmed: false);
        var passwordlessUserId = await CreateUserAsync(passwordlessEmail, passwordHash: null);
        var limitedUserId = await CreateUserAsync(limitedEmail);
        await SeedAccountLimitAsync(limitedUserId);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var unconfirmedClient = factory.CreateClient();
        using var passwordlessClient = factory.CreateClient();
        using var limitedClient = factory.CreateClient();

        var results = new[]
        {
            await PostAsync(unconfirmedClient, unconfirmedEmail),
            await PostAsync(passwordlessClient, passwordlessEmail),
            await PostAsync(limitedClient, limitedEmail),
        };

        Assert.All(
            results,
            result =>
            {
                Assert.Equal("/ForgotPassword", result.Location);
                Assert.Contains(GenericMessage, WebUtility.HtmlDecode(result.Html));
                AssertPrivateHeaders(result);
            });

        await using var verificationDb = _fixture.NewDbContext();
        var blockedUserIds = new[]
        {
            unconfirmedUserId,
            passwordlessUserId,
            limitedUserId,
        };
        Assert.False(await verificationDb.PasswordResetTokens.AnyAsync(
            token => blockedUserIds.Contains(token.UserId),
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            message => blockedUserIds.Contains(message.UserId)
                && message.Kind == AuthenticationEmailMessageKind.PasswordReset,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_WhenEmailIsInvalid_RendersValidationWithoutRecordingRequest()
    {
        var beforeCount = await CountAttemptsAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var pageResponse = await client.GetAsync(
            "/ForgotPassword",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "invalid-email",
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });

        using var response = await client.PostAsync(
            "/ForgotPassword",
            form,
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("사용할 수 없는 이메일 형식입니다.", html);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "no-referrer",
            response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(beforeCount, await CountAttemptsAsync());
    }

    [Fact]
    public async Task Post_WithoutAntiforgeryToken_IsRejectedWithoutIssuingToken()
    {
        var email = $"forgot-csrf-{Guid.NewGuid():N}@example.test";
        var userId = await CreateUserAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
        });

        using var response = await client.PostAsync(
            "/ForgotPassword",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.PasswordResetTokens.AnyAsync(
            token => token.UserId == userId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            message => message.UserId == userId
                && message.Kind == AuthenticationEmailMessageKind.PasswordReset,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.PasswordResetRequestAttempts.AnyAsync(
            attempt => attempt.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    private async Task<long> CreateUserAsync(
        string email,
        bool confirmed = true,
        string? passwordHash = "test-password-hash")
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(db, email, passwordHash);
        user.EmailConfirmedAt = confirmed ? DateTimeOffset.UtcNow.AddDays(-1) : null;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task SeedPendingResetAsync(
        long userId,
        string plaintextToken,
        DateTimeOffset createdAt)
    {
        await using var db = _fixture.NewDbContext();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = userId,
            TokenHash = HashToken(plaintextToken),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(PasswordResetTokenService.TokenLifetime),
        });
        new AuthenticationEmailOutboxService(
                db,
                TestAccountProtection.CreateMessageProtectionService(),
                new FixedTimeProvider(createdAt))
            .StagePasswordReset(
                userId,
                plaintextToken,
                createdAt.Add(PasswordResetTokenService.TokenLifetime));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedAccountLimitAsync(long userId)
    {
        var hash = TestAccountProtection.CreateRequestIpProtectionService()
            .CreateCurrentHash(IPAddress.Parse("198.51.100.90"));
        await using var db = _fixture.NewDbContext();
        db.PasswordResetRequestAttempts.AddRange(
            Enumerable.Range(0, PasswordResetRequestService.AccountHourlyLimit)
                .Select(_ => new PasswordResetRequestAttempt
                {
                    UserId = userId,
                    RequestIpHash = hash.Value.ToArray(),
                    RequestIpHashKeyId = hash.KeyId,
                    CreatedAt = DateTimeOffset.UtcNow,
                }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAttemptsAsync()
    {
        await using var db = _fixture.NewDbContext();
        return await db.PasswordResetRequestAttempts.CountAsync(
            TestContext.Current.CancellationToken);
    }

    private static async Task<PostResult> PostAsync(HttpClient client, string email)
    {
        using var pageResponse = await client.GetAsync(
            "/ForgotPassword",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });

        using var postResponse = await client.PostAsync(
            "/ForgotPassword",
            form,
            TestContext.Current.CancellationToken);
        var location = postResponse.Headers.Location?.OriginalString;
        Assert.Equal(HttpStatusCode.Redirect, postResponse.StatusCode);
        Assert.NotNull(location);
        var postCacheControl = postResponse.Headers.CacheControl?.ToString();
        var postReferrerPolicy = postResponse.Headers
            .GetValues("Referrer-Policy")
            .Single();

        using var resultResponse = await client.GetAsync(
            postResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var html = await resultResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        return new PostResult(
            location,
            postCacheControl,
            postReferrerPolicy,
            resultResponse.StatusCode,
            resultResponse.Headers.CacheControl?.ToString(),
            resultResponse.Headers.GetValues("Referrer-Policy").Single(),
            html);
    }

    private static void AssertPrivateHeaders(PostResult result)
    {
        Assert.Contains("no-store", result.PostCacheControl);
        Assert.Equal("no-referrer", result.PostReferrerPolicy);
        Assert.Contains("no-store", result.ResultCacheControl);
        Assert.Equal("no-referrer", result.ResultReferrerPolicy);
    }

    private static string CreatePlaintextToken(byte value) =>
        WebEncoders.Base64UrlEncode(
            Enumerable.Repeat(value, PasswordResetTokenService.TokenEntropySize)
                .ToArray());

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Unprotect(AuthenticationEmailOutboxMessage message) =>
        TestAccountProtection.CreateMessageProtectionService().Unprotect(
            message.UserId,
            message.Kind,
            new ProtectedAuthenticationEmailPayload(
                message.PayloadCiphertext,
                message.PayloadNonce,
                message.PayloadAuthenticationTag,
                message.PayloadKeyId));

    private sealed record PostResult(
        string Location,
        string? PostCacheControl,
        string PostReferrerPolicy,
        HttpStatusCode ResultStatusCode,
        string? ResultCacheControl,
        string ResultReferrerPolicy,
        string Html);

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
