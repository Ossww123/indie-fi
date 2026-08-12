using System.Net;
using IndieFi.Web.Entities;
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
            var ownerId = await arrangeDb.Users
                .Where(user => user.Email == email)
                .Select(user => user.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
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
