using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Feedback.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PublicProfilePageTests
{
    private readonly PostgreSqlFixture _fixture;

    public PublicProfilePageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_Anonymously_ShowsPublicFieldsGamesAndDeveloperProfileLink()
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        long userId;
        long gameId;
        await using (var db = _fixture.NewDbContext())
        {
            var user = await ReviewTestData.CreateUserWithCreditAsync(
                db,
                balance: 0,
                email);
            userId = user.Id;
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            db.UserProfiles.Add(new UserProfile
            {
                UserId = userId,
                Nickname = "Public Gamer",
                NormalizedNickname = "PUBLIC GAMER",
                Biography = "게임을 만들고 리뷰합니다.",
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
            var game = await ReviewTestData.CreateGameAsync(db, userId);
            game.Title = "Public Profile Game";
            game.Description = "공개 게임 소개";
            gameId = game.Id;
            db.ReviewerReputations.Add(new ReviewerReputation
            {
                ReviewerId = userId,
                CompletedReviewCount = 5,
                RatingCount = 3,
                RatingScoreSum = 15,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/Profiles/{userId}",
            TestContext.Current.CancellationToken);
        var rawHtml = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(rawHtml);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Public Gamer", html);
        Assert.Contains("게임을 만들고 리뷰합니다.", html);
        Assert.Contains("REVIEWS WRITTEN", html);
        Assert.Contains("<dd>5</dd>", html);
        Assert.Contains("REVIEWER GRADE", html);
        Assert.Contains("골드", html);
        Assert.DoesNotContain("신뢰도 점수", html);
        Assert.DoesNotContain("품질 점수", html);
        Assert.DoesNotContain("완료 안정성 점수", html);
        Assert.DoesNotContain("받은 평가 수", html);
        Assert.DoesNotContain("만료한 작업 수", html);
        Assert.Contains("Public Profile Game", html);
        Assert.Contains("공개 게임 소개", html);
        Assert.Contains($"href=\"/Games/Detail/{gameId}\"", html);
        Assert.Contains("data-default-profile-image", html);
        Assert.Contains("기본 프로필 이미지", html);
        Assert.Contains("로그인 후 팔로우", html);
        Assert.DoesNotContain("프로필 수정", html);
        Assert.DoesNotContain(email, html, StringComparison.OrdinalIgnoreCase);

        using var gameResponse = await client.GetAsync(
            $"/Games/Detail/{gameId}",
            TestContext.Current.CancellationToken);
        var gameHtml = WebUtility.HtmlDecode(
            await gameResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, gameResponse.StatusCode);
        Assert.Contains("by ", gameHtml);
        Assert.Contains("Public Gamer", gameHtml);
        Assert.Contains($"href=\"/Profiles/{userId}\"", gameHtml);
    }

    [Fact]
    public async Task Get_WithoutProfileOrGames_ShowsFallbackAndEmptyStates()
    {
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = (await ReviewTestData.CreateUserWithCreditAsync(
                db,
                balance: 0)).Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/Profiles/{userId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"user#{userId}", html);
        Assert.Contains("아직 소개가 없습니다.", html);
        Assert.Contains("아직 등록한 게임이 없습니다.", html);
        Assert.Contains("REVIEWS WRITTEN", html);
        Assert.Contains("<dd>0</dd>", html);
        Assert.Contains("REVIEWER GRADE", html);
        Assert.Contains("등급 산정 전", html);
    }

    [Fact]
    public async Task Get_EncodesBiographyAndGameFields()
    {
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
            userId = user.Id;
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            db.UserProfiles.Add(new UserProfile
            {
                UserId = userId,
                Nickname = "SafePlayer",
                NormalizedNickname = "SAFEPLAYER",
                Biography = "<script>alert('profile')</script>",
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
            var game = await ReviewTestData.CreateGameAsync(db, userId);
            game.Title = "<script>alert('game')</script>";
            game.Description = "<img src=x onerror=alert('description')>";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/Profiles/{userId}",
            TestContext.Current.CancellationToken);
        var rawHtml = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("&lt;script&gt;alert(&#x27;profile&#x27;)&lt;/script&gt;", rawHtml);
        Assert.Contains("&lt;script&gt;alert(&#x27;game&#x27;)&lt;/script&gt;", rawHtml);
        Assert.Contains(
            "&lt;img src=x onerror=alert(&#x27;description&#x27;)&gt;",
            rawHtml);
        Assert.DoesNotContain("<script>alert('profile')</script>", rawHtml);
        Assert.DoesNotContain("<script>alert('game')</script>", rawHtml);
        Assert.DoesNotContain("<img src=x onerror=alert('description')>", rawHtml);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    public async Task Get_WhenUserDoesNotExist_ReturnsNotFound(long userId)
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/Profiles/{userId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_AsOwner_LinksPublicAndEditProfilesBothWays()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
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

        using var publicResponse = await client.GetAsync(
            $"/Profiles/{userId}",
            TestContext.Current.CancellationToken);
        var publicHtml = WebUtility.HtmlDecode(
            await publicResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.Contains("프로필 수정", publicHtml);
        Assert.Contains("href=\"/Account/Profile\"", publicHtml);
        Assert.Contains("팔로잉 목록", publicHtml);
        Assert.Contains("href=\"/Account/Following\"", publicHtml);
        Assert.DoesNotContain(">팔로우</button>", publicHtml);

        using var editResponse = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var editHtml = WebUtility.HtmlDecode(
            await editResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        Assert.Contains("공개 프로필 보기", editHtml);
        Assert.Contains($"href=\"/Profiles/{userId}\"", editHtml);
        Assert.Contains("팔로잉 목록", editHtml);
        Assert.Contains("href=\"/Account/Following\"", editHtml);
    }
}
