using System.Net;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewQueuePageTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReviewQueuePageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_ReviewsPage_WhenSignedOut_RedirectsToLogin()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var loginPath = location.IsAbsoluteUri
            ? location.AbsolutePath
            : location.OriginalString.Split('?')[0];
        Assert.Equal("/Login", loginPath);
    }

    [Fact]
    public async Task Get_ReviewsPage_DoesNotShowMyOwnRequest()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var requesterId = await FindUserIdAsync(email);
        var (_, gameTitle) = await CreateReviewRequestAsync(requesterId, "내 게임");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(gameTitle, WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task Get_ReviewsPage_ShowsAnotherDevelopersRequest()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var (_, gameTitle) = await CreateReviewRequestAsync(null, "다른 개발자의 게임");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(gameTitle, WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task Get_ReviewsPage_OrdersOldestFirst()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var (_, firstGameTitle) = await CreateReviewRequestAsync(null, "첫 번째 게임");
        var (_, secondGameTitle) = await CreateReviewRequestAsync(null, "두 번째 게임");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decodedHtml = WebUtility.HtmlDecode(html);
        var firstPosition = decodedHtml.IndexOf(firstGameTitle, StringComparison.Ordinal);
        var secondPosition = decodedHtml.IndexOf(secondGameTitle, StringComparison.Ordinal);

        Assert.True(firstPosition >= 0, $"Expected to find '{firstGameTitle}' in the response.");
        Assert.True(secondPosition >= 0, $"Expected to find '{secondGameTitle}' in the response.");
        Assert.True(firstPosition < secondPosition, "Expected the older request to appear first.");
    }

    private async Task<long> FindUserIdAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        return await db.Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(long RequestId, string GameTitle)> CreateReviewRequestAsync(
        long? existingRequesterId,
        string titlePrefix)
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var requesterId = existingRequesterId
            ?? await CreateRequesterAsync(db, cancellationToken);

        // Credits have to arrive through the ledger: the application account may no
        // longer write credit_balance, so the database trigger is the only way to fund
        // the spend below.
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            UserId = requesterId,
            Kind = CreditEntryKind.Grant,
            Delta = 3,
        });

        var game = new Game
        {
            OwnerId = requesterId,
            Title = $"{titlePrefix} {Guid.NewGuid():N}",
        };

        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken);

        var spendEntry = new CreditLedgerEntry
        {
            UserId = requesterId,
            Kind = CreditEntryKind.Spend,
            Delta = -1,
        };
        var reviewRequest = new ReviewRequest
        {
            GameId = game.Id,
            RequesterId = requesterId,
            SpendEntry = spendEntry,
        };

        db.CreditLedgerEntries.Add(spendEntry);
        db.ReviewRequests.Add(reviewRequest);
        await db.SaveChangesAsync(cancellationToken);

        return (reviewRequest.Id, game.Title);
    }

    private static async Task<long> CreateRequesterAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var requester = new User
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "test-password-hash",
        };

        db.Users.Add(requester);

        // Saving here is what assigns the identity value the callers need.
        await db.SaveChangesAsync(cancellationToken);

        return requester.Id;
    }
}
