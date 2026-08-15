using System.Net;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ProfileEditPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public ProfileEditPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_WithoutLogin_RedirectsToLogin()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/Login?ReturnUrl=%2FAccount%2FProfile",
            response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Get_AfterLogin_ShowsEmptyProfileFormWithoutEmail()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var userId = await ReadUserIdAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name=\"Nickname\"", html);
        Assert.Contains("name=\"Biography\"", html);
        Assert.Contains("href=\"/Account/Profile\"", html);
        Assert.Contains(UserDisplayIdentifier.Format(userId), html);
        Assert.DoesNotContain(email, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_WithValidProfile_NormalizesStoresAndShowsSuccess()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var userId = await ReadUserIdAsync(email);
        var suffix = $"{Guid.NewGuid():N}"[..6];
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await PostProfileAsync(
            client,
            $"  Ｐａｇｅ{suffix}\t 게이머  ",
            "  첫 줄\r\n둘째 줄  ");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Profile", response.Headers.Location?.OriginalString);

        await using (var db = _fixture.NewDbContext())
        {
            var profile = await db.UserProfiles.SingleAsync(
                item => item.UserId == userId,
                TestContext.Current.CancellationToken);
            Assert.Equal($"Page{suffix} 게이머", profile.Nickname);
            Assert.Equal($"PAGE{suffix.ToUpperInvariant()} 게이머", profile.NormalizedNickname);
            Assert.Equal("첫 줄\n둘째 줄", profile.Biography);
        }

        using var getResponse = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await getResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Contains("프로필이 저장되었습니다.", html);
        Assert.Contains($"value=\"Page{suffix} 게이머\"", html);
        Assert.Contains("첫 줄\n둘째 줄", html);
    }

    [Fact]
    public async Task Post_WithInvalidFields_ShowsEveryErrorAndDoesNotCreateProfile()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var userId = await ReadUserIdAsync(email);
        var tooLongBiography = new string(
            '가',
            UserProfilePolicy.BiographyMaxLength + 1);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await PostProfileAsync(
            client,
            "player!",
            tooLongBiography);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "닉네임에는 한글, 영문, 숫자, 공백, 밑줄과 하이픈만 사용할 수 있습니다.",
            html);
        Assert.Contains("소개는 500자 이하여야 합니다.", html);
        Assert.Contains("value=\"player!\"", html);
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.UserProfiles.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_WithUsedNickname_ShowsFriendlyError()
    {
        var first = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var second = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var firstUserId = await ReadUserIdAsync(first.Email);
        var secondUserId = await ReadUserIdAsync(second.Email);
        var nickname = $"Page{Guid.NewGuid():N}"[..12];
        await AddProfileAsync(firstUserId, nickname, DateTimeOffset.UtcNow.AddDays(-8));
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, second.Email, second.Password);

        using var response = await PostProfileAsync(
            client,
            nickname.ToLowerInvariant(),
            "두 번째 소개");
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("이미 사용 중인 닉네임입니다.", html);
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.UserProfiles.AnyAsync(
            item => item.UserId == secondUserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_DuringNicknameCooldown_KeepsNicknameReadonlyAndBiographyEditable()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var userId = await ReadUserIdAsync(email);
        var nickname = $"Page{Guid.NewGuid():N}"[..12];
        await AddProfileAsync(userId, nickname, DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var getResponse = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var getHtml = WebUtility.HtmlDecode(
            await getResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Contains("readonly=\"readonly\"", getHtml);
        Assert.Contains("다음 변경 가능 시각", getHtml);

        using var postResponse = await PostProfileAsync(
            client,
            nickname,
            "변경한 소개");
        Assert.Equal(HttpStatusCode.Redirect, postResponse.StatusCode);
        await using var db = _fixture.NewDbContext();
        var profile = await db.UserProfiles.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(nickname, profile.Nickname);
        Assert.Equal("변경한 소개", profile.Biography);
    }

    [Fact]
    public async Task Post_ChangingNicknameDuringCooldown_ShowsErrorAndPreservesProfile()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var userId = await ReadUserIdAsync(email);
        var nickname = $"Page{Guid.NewGuid():N}"[..12];
        await AddProfileAsync(userId, nickname, DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        var proposedNickname = $"New{Guid.NewGuid():N}"[..12];
        using var response = await PostProfileAsync(
            client,
            proposedNickname,
            "바꾸려던 소개");
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("닉네임은 7일마다 변경하거나 해제할 수 있습니다.", html);
        Assert.Contains("다음 변경 가능 시각", html);
        Assert.Contains($"value=\"{nickname}\"", html);
        Assert.DoesNotContain($"value=\"{proposedNickname}\"", html);
        await using var db = _fixture.NewDbContext();
        var profile = await db.UserProfiles.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(nickname, profile.Nickname);
        Assert.Empty(profile.Biography);
    }

    [Fact]
    public async Task Post_WithoutAntiforgeryToken_RejectsRequestAndLeavesProfileUnchanged()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var userId = await ReadUserIdAsync(email);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Nickname"] = "CsrfPlayer",
            ["Biography"] = "저장되면 안 되는 소개",
        });

        using var response = await client.PostAsync(
            "/Account/Profile",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = _fixture.NewDbContext();
        Assert.False(await db.UserProfiles.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    private async Task<HttpResponseMessage> PostProfileAsync(
        HttpClient client,
        string nickname,
        string biography)
    {
        using var pageResponse = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Nickname"] = nickname,
            ["Biography"] = biography,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        return await client.PostAsync(
            "/Account/Profile",
            form,
            TestContext.Current.CancellationToken);
    }

    private async Task<long> ReadUserIdAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task AddProfileAsync(
        long userId,
        string nickname,
        DateTimeOffset nicknameChangedAt)
    {
        await using var db = _fixture.NewDbContext();
        db.UserProfiles.Add(new UserProfile
        {
            UserId = userId,
            Nickname = nickname,
            NormalizedNickname = nickname.ToUpperInvariant(),
            NicknameChangedAt = nicknameChangedAt,
            CreatedAt = nicknameChangedAt,
            UpdatedAt = nicknameChangedAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
