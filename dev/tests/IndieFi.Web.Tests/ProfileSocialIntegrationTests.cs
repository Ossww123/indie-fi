using System.Net;
using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ProfileSocialIntegrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public ProfileSocialIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProfileRename_UpdatesPublicFollowAndFavoriteViewsWithoutBreakingRelationships()
    {
        var data = await CreateSocialDataAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var memberClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(
            memberClient,
            data.MemberEmail,
            data.MemberPassword);

        using (var followResponse = await PostFromPageAsync(
            memberClient,
            $"/Profiles/{data.CreatorId}",
            $"/Profiles/{data.CreatorId}?handler=Follow"))
        {
            Assert.Equal(HttpStatusCode.Redirect, followResponse.StatusCode);
        }

        using (var favoriteResponse = await PostFromPageAsync(
            memberClient,
            $"/Games/Detail/{data.GameId}",
            $"/Games/Detail/{data.GameId}?handler=Favorite"))
        {
            Assert.Equal(HttpStatusCode.Redirect, favoriteResponse.StatusCode);
        }

        using var creatorClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(
            creatorClient,
            data.CreatorEmail,
            data.CreatorPassword);
        using var profileResponse = await creatorClient.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var profileHtml = await profileResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var profileForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Nickname"] = $"  Ｎｅｗ{data.Suffix}\t Dev  ",
            ["Biography"] = "통합 검증에서 바꾼 공개 소개",
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(profileHtml),
        });
        using var updateResponse = await creatorClient.PostAsync(
            "/Account/Profile",
            profileForm,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, updateResponse.StatusCode);

        var expectedNickname = $"New{data.Suffix} Dev";
        using var publicResponse = await memberClient.GetAsync(
            $"/Profiles/{data.CreatorId}",
            TestContext.Current.CancellationToken);
        var publicHtml = WebUtility.HtmlDecode(
            await publicResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.Contains(expectedNickname, publicHtml);
        Assert.Contains("통합 검증에서 바꾼 공개 소개", publicHtml);
        Assert.Contains(">팔로우 해제</button>", publicHtml);
        Assert.DoesNotContain(data.CreatorEmail, publicHtml, StringComparison.OrdinalIgnoreCase);

        using var followingResponse = await memberClient.GetAsync(
            "/Account/Following",
            TestContext.Current.CancellationToken);
        var followingHtml = WebUtility.HtmlDecode(
            await followingResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains(expectedNickname, followingHtml);
        Assert.DoesNotContain(data.InitialNickname, followingHtml);

        using var favoritesResponse = await memberClient.GetAsync(
            "/Account/FavoriteGames",
            TestContext.Current.CancellationToken);
        var favoritesHtml = WebUtility.HtmlDecode(
            await favoritesResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains(data.GameTitle, favoritesHtml);
        Assert.Contains(expectedNickname, favoritesHtml);
        Assert.DoesNotContain(data.InitialNickname, favoritesHtml);

        await using var db = _fixture.NewDbContext();
        var profile = await db.UserProfiles.SingleAsync(
            item => item.UserId == data.CreatorId,
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedNickname, profile.Nickname);
        Assert.Equal(expectedNickname.ToUpperInvariant(), profile.NormalizedNickname);
        Assert.Equal(1, await db.UserFollows.CountAsync(
            item => item.FollowerId == data.MemberId
                && item.FollowedUserId == data.CreatorId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.FavoriteGames.CountAsync(
            item => item.UserId == data.MemberId && item.GameId == data.GameId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RelationshipChanges_AreScopedToCurrentUserAndActorsStayPrivate()
    {
        var data = await CreateSocialDataAsync();
        var stranger = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        long strangerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            strangerId = (await TestAccountProtection.FindUserAsync(
                arrangeDb,
                stranger.Email,
                TestContext.Current.CancellationToken)).Id;
            await new UserFollowService(arrangeDb).FollowAsync(
                data.MemberId,
                data.CreatorId,
                TestContext.Current.CancellationToken);
            await new FavoriteGameService(arrangeDb).FavoriteAsync(
                data.MemberId,
                data.GameId,
                TestContext.Current.CancellationToken);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var strangerClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(
            strangerClient,
            stranger.Email,
            stranger.Password);

        using (var unfollowResponse = await PostFromPageAsync(
            strangerClient,
            $"/Profiles/{data.CreatorId}",
            $"/Profiles/{data.CreatorId}?handler=Unfollow"))
        {
            Assert.Equal(HttpStatusCode.Redirect, unfollowResponse.StatusCode);
        }

        using (var unfavoriteResponse = await PostFromPageAsync(
            strangerClient,
            $"/Games/Detail/{data.GameId}",
            $"/Games/Detail/{data.GameId}?handler=Unfavorite"))
        {
            Assert.Equal(HttpStatusCode.Redirect, unfavoriteResponse.StatusCode);
        }

        using var followingResponse = await strangerClient.GetAsync(
            "/Account/Following",
            TestContext.Current.CancellationToken);
        var followingHtml = WebUtility.HtmlDecode(
            await followingResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("아직 팔로우한 사용자가 없습니다.", followingHtml);
        Assert.DoesNotContain(data.InitialNickname, followingHtml);

        using var favoritesResponse = await strangerClient.GetAsync(
            "/Account/FavoriteGames",
            TestContext.Current.CancellationToken);
        var favoritesHtml = WebUtility.HtmlDecode(
            await favoritesResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("아직 관심 게임이 없습니다.", favoritesHtml);
        Assert.DoesNotContain(data.GameTitle, favoritesHtml);

        using var anonymousClient = factory.CreateClient();
        using var publicProfileResponse = await anonymousClient.GetAsync(
            $"/Profiles/{data.CreatorId}",
            TestContext.Current.CancellationToken);
        var publicProfileHtml = WebUtility.HtmlDecode(
            await publicProfileResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        using var publicGameResponse = await anonymousClient.GetAsync(
            $"/Games/Detail/{data.GameId}",
            TestContext.Current.CancellationToken);
        var publicGameHtml = WebUtility.HtmlDecode(
            await publicGameResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            $"user#{data.MemberId}",
            publicProfileHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            $"user#{data.MemberId}",
            publicGameHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            data.MemberEmail,
            publicProfileHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            data.MemberEmail,
            publicGameHtml,
            StringComparison.OrdinalIgnoreCase);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.True(await verificationDb.UserFollows.AnyAsync(
            item => item.FollowerId == data.MemberId
                && item.FollowedUserId == data.CreatorId,
            TestContext.Current.CancellationToken));
        Assert.True(await verificationDb.FavoriteGames.AnyAsync(
            item => item.UserId == data.MemberId && item.GameId == data.GameId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.UserFollows.AnyAsync(
            item => item.FollowerId == strangerId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.FavoriteGames.AnyAsync(
            item => item.UserId == strangerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentRepeatedFollowAndFavoriteRequests_ConvergeIndependently()
    {
        var data = await CreateSocialDataAsync();
        await using var firstFollowDb = _fixture.NewDbContext();
        await using var secondFollowDb = _fixture.NewDbContext();
        await using var firstFavoriteDb = _fixture.NewDbContext();
        await using var secondFavoriteDb = _fixture.NewDbContext();
        var firstFollowService = new UserFollowService(firstFollowDb);
        var secondFollowService = new UserFollowService(secondFollowDb);
        var firstFavoriteService = new FavoriteGameService(firstFavoriteDb);
        var secondFavoriteService = new FavoriteGameService(secondFavoriteDb);

        var firstFollow = firstFollowService.FollowAsync(
            data.MemberId,
            data.CreatorId,
            TestContext.Current.CancellationToken);
        var secondFollow = secondFollowService.FollowAsync(
            data.MemberId,
            data.CreatorId,
            TestContext.Current.CancellationToken);
        var firstFavorite = firstFavoriteService.FavoriteAsync(
            data.MemberId,
            data.GameId,
            TestContext.Current.CancellationToken);
        var secondFavorite = secondFavoriteService.FavoriteAsync(
            data.MemberId,
            data.GameId,
            TestContext.Current.CancellationToken);
        await Task.WhenAll(firstFollow, secondFollow, firstFavorite, secondFavorite);

        Assert.Single(
            new[] { await firstFollow, await secondFollow },
            result => result.Changed);
        Assert.Single(
            new[] { await firstFavorite, await secondFavorite },
            result => result.Changed);

        var firstUnfollow = firstFollowService.UnfollowAsync(
            data.MemberId,
            data.CreatorId,
            TestContext.Current.CancellationToken);
        var secondUnfollow = secondFollowService.UnfollowAsync(
            data.MemberId,
            data.CreatorId,
            TestContext.Current.CancellationToken);
        var firstUnfavorite = firstFavoriteService.UnfavoriteAsync(
            data.MemberId,
            data.GameId,
            TestContext.Current.CancellationToken);
        var secondUnfavorite = secondFavoriteService.UnfavoriteAsync(
            data.MemberId,
            data.GameId,
            TestContext.Current.CancellationToken);
        await Task.WhenAll(
            firstUnfollow,
            secondUnfollow,
            firstUnfavorite,
            secondUnfavorite);

        Assert.Single(
            new[] { await firstUnfollow, await secondUnfollow },
            result => result.Changed);
        Assert.Single(
            new[] { await firstUnfavorite, await secondUnfavorite },
            result => result.Changed);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.UserFollows.AnyAsync(
            item => item.FollowerId == data.MemberId
                && item.FollowedUserId == data.CreatorId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.FavoriteGames.AnyAsync(
            item => item.UserId == data.MemberId && item.GameId == data.GameId,
            TestContext.Current.CancellationToken));
    }

    private async Task<SocialData> CreateSocialDataAsync()
    {
        var creator = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var member = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var suffix = $"{Guid.NewGuid():N}"[..6];
        var initialNickname = $"Creator{suffix}";
        var gameTitle = $"Social Game {suffix}";

        await using var db = _fixture.NewDbContext();
        var creatorId = (await TestAccountProtection.FindUserAsync(
            db,
            creator.Email,
            TestContext.Current.CancellationToken)).Id;
        var memberId = (await TestAccountProtection.FindUserAsync(
            db,
            member.Email,
            TestContext.Current.CancellationToken)).Id;
        var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
        db.UserProfiles.Add(new UserProfile
        {
            UserId = creatorId,
            Nickname = initialNickname,
            NormalizedNickname = initialNickname.ToUpperInvariant(),
            Biography = "통합 검증 전 공개 소개",
            NicknameChangedAt = changedAt,
            CreatedAt = changedAt,
            UpdatedAt = changedAt,
        });
        var game = await ReviewTestData.CreateGameAsync(db, creatorId);
        game.Title = gameTitle;
        game.Description = "통합 검증 게임 소개";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new SocialData(
            creator.Email,
            creator.Password,
            member.Email,
            member.Password,
            creatorId,
            memberId,
            game.Id,
            suffix,
            initialNickname,
            gameTitle);
    }

    private static async Task<HttpResponseMessage> PostFromPageAsync(
        HttpClient client,
        string pageUrl,
        string postUrl)
    {
        using var pageResponse = await client.GetAsync(
            pageUrl,
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        return await client.PostAsync(
            postUrl,
            form,
            TestContext.Current.CancellationToken);
    }

    private sealed record SocialData(
        string CreatorEmail,
        string CreatorPassword,
        string MemberEmail,
        string MemberPassword,
        long CreatorId,
        long MemberId,
        long GameId,
        string Suffix,
        string InitialNickname,
        string GameTitle);
}
