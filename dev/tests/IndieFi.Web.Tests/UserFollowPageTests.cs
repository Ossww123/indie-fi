using System.Net;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class UserFollowPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public UserFollowPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublicProfile_AnonymousUserSeesLoginLinkAndCannotFollow()
    {
        var pair = await CreateUserPairAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var profileResponse = await client.GetAsync(
            $"/Profiles/{pair.TargetId}",
            TestContext.Current.CancellationToken);
        var profileHtml = WebUtility.HtmlDecode(
            await profileResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        Assert.Contains("로그인 후 팔로우", profileHtml);
        Assert.Contains(
            $"ReturnUrl=%2FProfiles%2F{pair.TargetId}",
            profileHtml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form method=\"post\"", profileHtml);

        using var loginResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var loginHtml = await loginResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var followResponse = await PostRelationshipAsync(
            client,
            pair.TargetId,
            "Follow",
            PageTestSupport.ExtractAntiforgeryToken(loginHtml));

        Assert.Equal(HttpStatusCode.Redirect, followResponse.StatusCode);
        Assert.StartsWith(
            "/Login?ReturnUrl=",
            followResponse.Headers.Location?.PathAndQuery);
        await AssertRelationshipCountAsync(pair.FollowerId, pair.TargetId, 0);
    }

    [Fact]
    public async Task FollowingList_AnonymousUserIsRedirectedToLogin()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Account/Following",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "/Login?ReturnUrl=%2FAccount%2FFollowing",
            response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task FollowAndUnfollow_RepeatedRequestsConvergeAndUpdatePrivateList()
    {
        var pair = await CreateUserPairAsync(addTargetProfile: true);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, pair.Email, pair.Password);

        using var initialResponse = await client.GetAsync(
            $"/Profiles/{pair.TargetId}",
            TestContext.Current.CancellationToken);
        var initialHtml = WebUtility.HtmlDecode(
            await initialResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        var initialToken = PageTestSupport.ExtractAntiforgeryToken(initialHtml);
        Assert.Contains(">팔로우</button>", initialHtml);
        Assert.DoesNotContain("로그인 후 팔로우", initialHtml);

        using (var firstFollow = await PostRelationshipAsync(
            client,
            pair.TargetId,
            "Follow",
            initialToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, firstFollow.StatusCode);
            Assert.Equal(
                $"/Profiles/{pair.TargetId}?relationship=followed",
                firstFollow.Headers.Location?.OriginalString);
        }

        using (var repeatedFollow = await PostRelationshipAsync(
            client,
            pair.TargetId,
            "Follow",
            initialToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, repeatedFollow.StatusCode);
        }

        await AssertRelationshipCountAsync(pair.FollowerId, pair.TargetId, 1);

        using var followedProfileResponse = await client.GetAsync(
            $"/Profiles/{pair.TargetId}?relationship=followed",
            TestContext.Current.CancellationToken);
        var followedProfileHtml = WebUtility.HtmlDecode(
            await followedProfileResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("팔로우했습니다.", followedProfileHtml);
        Assert.Contains(">팔로우 해제</button>", followedProfileHtml);

        using var listResponse = await client.GetAsync(
            "/Account/Following",
            TestContext.Current.CancellationToken);
        var listHtml = WebUtility.HtmlDecode(
            await listResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains(pair.TargetNickname!, listHtml);
        Assert.Contains("팔로우한 사용자 소개", listHtml);
        Assert.Contains($"href=\"/Profiles/{pair.TargetId}\"", listHtml);

        var unfollowToken = PageTestSupport.ExtractAntiforgeryToken(followedProfileHtml);
        using (var firstUnfollow = await PostRelationshipAsync(
            client,
            pair.TargetId,
            "Unfollow",
            unfollowToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, firstUnfollow.StatusCode);
            Assert.Equal(
                $"/Profiles/{pair.TargetId}?relationship=unfollowed",
                firstUnfollow.Headers.Location?.OriginalString);
        }

        using (var repeatedUnfollow = await PostRelationshipAsync(
            client,
            pair.TargetId,
            "Unfollow",
            unfollowToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, repeatedUnfollow.StatusCode);
        }

        await AssertRelationshipCountAsync(pair.FollowerId, pair.TargetId, 0);

        using var emptyListResponse = await client.GetAsync(
            "/Account/Following",
            TestContext.Current.CancellationToken);
        var emptyListHtml = WebUtility.HtmlDecode(
            await emptyListResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("아직 팔로우한 사용자가 없습니다.", emptyListHtml);
        Assert.DoesNotContain(pair.TargetNickname!, emptyListHtml);
    }

    [Fact]
    public async Task Follow_Self_ReturnsBadRequestAndOwnProfileHasNoFollowForm()
    {
        var pair = await CreateUserPairAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, pair.Email, pair.Password);

        using var profileResponse = await client.GetAsync(
            $"/Profiles/{pair.FollowerId}",
            TestContext.Current.CancellationToken);
        var profileHtml = WebUtility.HtmlDecode(
            await profileResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("프로필 수정", profileHtml);
        Assert.Contains("팔로잉 목록", profileHtml);
        Assert.DoesNotContain(">팔로우</button>", profileHtml);
        Assert.DoesNotContain(">팔로우 해제</button>", profileHtml);

        using var response = await PostRelationshipAsync(
            client,
            pair.FollowerId,
            "Follow",
            PageTestSupport.ExtractAntiforgeryToken(profileHtml));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRelationshipCountAsync(pair.FollowerId, pair.FollowerId, 0);
    }

    [Fact]
    public async Task Follow_MissingTarget_ReturnsNotFound()
    {
        var pair = await CreateUserPairAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, pair.Email, pair.Password);
        using var profileResponse = await client.GetAsync(
            $"/Profiles/{pair.TargetId}",
            TestContext.Current.CancellationToken);
        var profileHtml = await profileResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        using var response = await PostRelationshipAsync(
            client,
            long.MaxValue,
            "Follow",
            PageTestSupport.ExtractAntiforgeryToken(profileHtml));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Follow_WithoutAntiforgeryToken_ReturnsBadRequestAndDoesNotChangeState()
    {
        var pair = await CreateUserPairAsync();
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, pair.Email, pair.Password);
        using var form = new FormUrlEncodedContent([]);

        using var response = await client.PostAsync(
            $"/Profiles/{pair.TargetId}?handler=Follow",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRelationshipCountAsync(pair.FollowerId, pair.TargetId, 0);
    }

    private async Task<UserPair> CreateUserPairAsync(bool addTargetProfile = false)
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        var target = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var db = _fixture.NewDbContext();
        var followerId = (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
        var targetId = (await TestAccountProtection.FindUserAsync(
            db,
            target.Email,
            TestContext.Current.CancellationToken)).Id;
        string? targetNickname = null;
        if (addTargetProfile)
        {
            var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
            targetNickname = $"Followed{Guid.NewGuid():N}"[..16];
            db.UserProfiles.Add(new UserProfile
            {
                UserId = targetId,
                Nickname = targetNickname,
                NormalizedNickname = targetNickname.ToUpperInvariant(),
                Biography = "팔로우한 사용자 소개",
                NicknameChangedAt = changedAt,
                CreatedAt = changedAt,
                UpdatedAt = changedAt,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return new UserPair(email, password, followerId, targetId, targetNickname);
    }

    private static async Task<HttpResponseMessage> PostRelationshipAsync(
        HttpClient client,
        long targetId,
        string handler,
        string antiforgeryToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
        });
        return await client.PostAsync(
            $"/Profiles/{targetId}?handler={handler}",
            form,
            TestContext.Current.CancellationToken);
    }

    private async Task AssertRelationshipCountAsync(
        long followerId,
        long followedUserId,
        int expectedCount)
    {
        await using var db = _fixture.NewDbContext();
        Assert.Equal(expectedCount, await db.UserFollows.CountAsync(
            item => item.FollowerId == followerId
                && item.FollowedUserId == followedUserId,
            TestContext.Current.CancellationToken));
    }

    private sealed record UserPair(
        string Email,
        string Password,
        long FollowerId,
        long TargetId,
        string? TargetNickname);
}
