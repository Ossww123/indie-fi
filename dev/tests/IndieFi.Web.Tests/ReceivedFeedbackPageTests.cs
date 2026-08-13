using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReceivedFeedbackPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReceivedFeedbackPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_FeedbackPage_AsOwner_ShowsBodyAndReviewerEmail()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var feedbackBody = $"소유자에게 보이는 충분히 긴 피드백 본문입니다. 식별자 {Guid.NewGuid():N}";
        var feedback = await CreateGameWithFeedbackAsync(ownerId, feedbackBody);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Feedback/{feedback.GameId}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decodedHtml = WebUtility.HtmlDecode(html);
        Assert.Contains(feedback.GameTitle, decodedHtml);
        Assert.Contains(feedbackBody, decodedHtml);
        Assert.Contains(feedback.ReviewerEmail, decodedHtml);
    }

    [Fact]
    public async Task Get_FeedbackPage_DoesNotShowAnotherGamesFeedback()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var firstBody = $"첫 번째 게임에만 속해야 하는 충분히 긴 피드백입니다. {Guid.NewGuid():N}";
        var secondBody = $"두 번째 게임에만 속해야 하는 충분히 긴 피드백입니다. {Guid.NewGuid():N}";
        var firstFeedback = await CreateGameWithFeedbackAsync(ownerId, firstBody);
        await CreateGameWithFeedbackAsync(ownerId, secondBody);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Feedback/{firstFeedback.GameId}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decodedHtml = WebUtility.HtmlDecode(html);
        Assert.Contains(firstBody, decodedHtml);
        Assert.DoesNotContain(secondBody, decodedHtml);
    }

    [Fact]
    public async Task Get_FeedbackPage_ForAnotherUsersGame_ReturnsNotFound()
    {
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
            gameId = (await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id)).Id;
        }

        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Feedback/{gameId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_FeedbackPage_ForMissingGame_ReturnsNotFound()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Feedback/{long.MaxValue}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_FeedbackPage_WhenSignedOut_RedirectsToLogin()
    {
        await using var arrangeDb = _fixture.NewDbContext();
        var owner = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(arrangeDb, owner.Id);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/Games/Feedback/{game.Id}",
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
    public async Task Get_FeedbackPage_WithNoFeedback_ShowsEmptyMessage()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        long gameId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            gameId = (await ReviewTestData.CreateGameAsync(arrangeDb, ownerId)).Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Feedback/{gameId}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("아직 받은 피드백이 없습니다.", WebUtility.HtmlDecode(html));
    }

    private async Task<long> FindUserIdAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        return await db.Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<FeedbackTestData> CreateGameWithFeedbackAsync(
        long ownerId,
        string body)
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Funding through the ledger keeps the trigger-maintained balance consistent.
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            UserId = ownerId,
            Kind = CreditEntryKind.Grant,
            Delta = 1,
        });
        await db.SaveChangesAsync(cancellationToken);

        var reviewer = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var game = await ReviewTestData.CreateGameAsync(db, ownerId);
        var reviewRequest = await ReviewTestData.CreateQueuedRequestAsync(
            db,
            ownerId,
            game.Id);

        reviewRequest.Status = ReviewRequestStatus.Completed;
        var assignment = new ReviewAssignment
        {
            RequestId = reviewRequest.Id,
            GameId = game.Id,
            ReviewerId = reviewer.Id,
            Status = ReviewAssignmentStatus.Completed,
            LeasedUntil = DateTimeOffset.UtcNow.AddHours(1),
        };
        db.ReviewAssignments.Add(assignment);
        await db.SaveChangesAsync(cancellationToken);

        // The read page does not need queue arbitration, so inserting the final row directly
        // keeps setup deterministic while preserving every database relationship and constraint.
        db.FeedbackSubmissions.Add(new FeedbackSubmission
        {
            AssignmentId = assignment.Id,
            Body = body,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new FeedbackTestData(game.Id, game.Title, reviewer.Email);
    }

    private sealed record FeedbackTestData(
        long GameId,
        string GameTitle,
        string ReviewerEmail);
}
