using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameDetailPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameDetailPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_GameDetail_AsOwner_ShowsRequestListAndOnlyQueuedCancellationButton()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        long gameId;
        long queuedRequestId;
        long cancelledRequestId;
        string queuedCreatedAt;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var ownerId = (await TestAccountProtection.FindUserAsync(
                arrangeDb,
                email,
                TestContext.Current.CancellationToken)).Id;
            arrangeDb.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                UserId = ownerId,
                Kind = CreditEntryKind.Grant,
                Delta = 4,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            var game = await ReviewTestData.CreateGameAsync(arrangeDb, ownerId);
            gameId = game.Id;
            var queuedRequest = await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                ownerId,
                gameId);
            queuedRequestId = queuedRequest.Id;
            queuedCreatedAt = queuedRequest.CreatedAt
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm");
            var assignedRequest = await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                ownerId,
                gameId);
            var completedRequest = await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                ownerId,
                gameId);
            var cancelledRequest = await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                ownerId,
                gameId);
            cancelledRequestId = cancelledRequest.Id;

            assignedRequest.Status = ReviewRequestStatus.Assigned;
            completedRequest.Status = ReviewRequestStatus.Completed;
            cancelledRequest.Status = ReviewRequestStatus.Cancelled;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Detail/{gameId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("피드백 요청", html);
        Assert.Contains("대기 중", html);
        Assert.Contains("배정됨", html);
        Assert.Contains("완료", html);
        Assert.Contains(queuedCreatedAt, html);
        Assert.DoesNotContain("Cancelled", html);
        Assert.Contains("handler=CancelRequest", html);
        Assert.Contains($"requestId={queuedRequestId}", html);
        Assert.DoesNotContain($"requestId={cancelledRequestId}", html);
        Assert.Equal(1, CountOccurrences(html, ">취소</button>"));

        var completedPosition = html.IndexOf("완료", StringComparison.Ordinal);
        var assignedPosition = html.IndexOf("배정됨", StringComparison.Ordinal);
        var queuedPosition = html.IndexOf("대기 중", StringComparison.Ordinal);
        Assert.True(completedPosition < assignedPosition);
        Assert.True(assignedPosition < queuedPosition);
    }

    [Fact]
    public async Task Get_GameDetail_AsAnotherUser_ShowsOnlyQueuedRequestCount()
    {
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 2);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            gameId = game.Id;
            await ReviewTestData.CreateQueuedRequestAsync(arrangeDb, owner.Id, gameId);
            await ReviewTestData.CreateQueuedRequestAsync(arrangeDb, owner.Id, gameId);
        }

        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Detail/{gameId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("대기 중인 피드백 요청 2개", html);
        Assert.DoesNotContain("<h2 class=\"h5 mt-4\">피드백 요청</h2>", html);
        Assert.DoesNotContain("handler=CancelRequest", html);
        Assert.DoesNotContain(">취소</button>", html);
        Assert.DoesNotContain("배정됨", html);
        Assert.DoesNotContain("완료", html);
    }

    [Fact]
    public async Task Get_GameDetail_Anonymously_ShowsOrderedProtectedExternalLinks()
    {
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);
            gameId = game.Id;
            arrangeDb.GamePlayLinks.AddRange(
                new GamePlayLink
                {
                    GameId = gameId,
                    DisplayName = "Second link",
                    Provider = GamePlayLinkProvider.Steam,
                    Url = "https://store.steampowered.com/app/123",
                    SortOrder = 1,
                },
                new GamePlayLink
                {
                    GameId = gameId,
                    DisplayName = "<script>alert(1)</script> First browser link",
                    Provider = GamePlayLinkProvider.DeveloperWebsite,
                    TargetPlatform = GamePlayLinkPlatform.Web,
                    Url = "https://example.com/play?mode=demo&source=indiefi",
                    SortOrder = 0,
                });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/Games/Detail/{gameId}",
            TestContext.Current.CancellationToken);
        var rawHtml = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(rawHtml);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("플레이하기", html);
        Assert.Contains("외부 사이트의 안전성이나 이용 가능 여부를 보증하지 않습니다.", html);
        Assert.Contains("개발자 사이트", html);
        Assert.Contains("Web", html);
        Assert.Equal(2, CountOccurrences(rawHtml, "target=\"_blank\""));
        Assert.Equal(2, CountOccurrences(rawHtml, "rel=\"noopener noreferrer\""));
        Assert.Contains(
            "href=\"https://example.com/play?mode=demo&amp;source=indiefi\"",
            rawHtml);
        Assert.Contains(
            "&lt;script&gt;alert(1)&lt;/script&gt; First browser link",
            rawHtml);
        Assert.DoesNotContain("<script>alert(1)</script>", rawHtml);
        Assert.True(
            html.IndexOf("<script>alert(1)</script> First browser link", StringComparison.Ordinal)
            < html.IndexOf("Second link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_GameDetail_WithoutLinks_ShowsEmptyStateWithoutExternalWarning()
    {
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
            gameId = (await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id)).Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/Games/Detail/{gameId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("등록된 외부 플레이 링크 없음", html);
        Assert.DoesNotContain("외부 사이트의 안전성이나 이용 가능 여부를 보증하지 않습니다.", html);
        Assert.DoesNotContain("target=\"_blank\"", html);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var position = 0;

        while ((position = source.IndexOf(value, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }

        return count;
    }
}
