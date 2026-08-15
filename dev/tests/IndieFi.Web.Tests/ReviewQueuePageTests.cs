using System.Net;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        var request = await CreateReviewRequestAsync(requesterId, "내 게임");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(request.GameTitle, WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task Get_ReviewsPage_ShowsRequesterNicknameAndProfileLink()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var request = await CreateReviewRequestAsync(null, "다른 개발자의 게임");
        var requesterNickname = $"Requester{Guid.NewGuid():N}"[..15];
        await AddProfileAsync(request.RequesterId, requesterNickname);

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
        Assert.Contains(request.GameTitle, decodedHtml);
        Assert.Contains(requesterNickname, decodedHtml);
        Assert.Contains($"href=\"/Profiles/{request.RequesterId}\"", decodedHtml);
        Assert.DoesNotContain(
            UserDisplayIdentifier.Format(request.RequesterId),
            decodedHtml);
        Assert.DoesNotContain(request.RequesterEmail, decodedHtml);
        Assert.True(Guid.TryParse(
            PageTestSupport.ExtractHiddenInput(html, "operationId"),
            out var claimOperationId));
        Assert.NotEqual(Guid.Empty, claimOperationId);
    }

    [Fact]
    public async Task Get_ReviewsPage_OrdersOldestFirst()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var firstRequest = await CreateReviewRequestAsync(null, "첫 번째 게임");
        var secondRequest = await CreateReviewRequestAsync(null, "두 번째 게임");

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
        var firstPosition = decodedHtml.IndexOf(firstRequest.GameTitle, StringComparison.Ordinal);
        var secondPosition = decodedHtml.IndexOf(secondRequest.GameTitle, StringComparison.Ordinal);

        Assert.True(
            firstPosition >= 0,
            $"Expected to find '{firstRequest.GameTitle}' in the response.");
        Assert.True(
            secondPosition >= 0,
            $"Expected to find '{secondRequest.GameTitle}' in the response.");
        Assert.True(firstPosition < secondPosition, "Expected the older request to appear first.");
    }

    [Fact]
    public async Task SubmitPage_WhenTheAssignmentBelongsToSomeoneElse_RedirectsToTheList()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        long reviewerId;
        long requestId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            // Claiming reads the whole queue, not one game, so leftovers from other tests would
            // be handed out instead of the rows this test arranged. Assignments disappear with
            // their requests through the cascade.
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            var requester = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 1);
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            requestId = (await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id)).Id;
        }

        long assignmentId;
        await using (var claimDb = _fixture.NewDbContext())
        {
            var claimResult = await new ReviewClaimService(
                claimDb,
                NullLogger<ReviewClaimService>.Instance).ClaimNextAsync(reviewerId);
            assignmentId = await claimDb.ReviewAssignments
                .Where(assignment => assignment.RequestId == requestId)
                .Select(assignment => assignment.Id)
                .SingleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(ReviewClaimOutcome.Claimed, claimResult.Outcome);
            Assert.Equal(requestId, claimResult.RequestId);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Reviews/Submit/{assignmentId}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var reviewsPath = location.IsAbsoluteUri
            ? location.AbsolutePath
            : location.OriginalString.Split('?')[0];
        Assert.Equal("/Reviews", reviewsPath);
    }

    [Fact]
    public async Task Post_Claim_WithoutOperationId_IsRejected()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var reviewerId = await FindUserIdAsync(email);
        await CreateReviewRequestAsync(null, "Missing operation ID game");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var reviewsResponse = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);
        var reviewsHtml = await reviewsResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(reviewsHtml),
        });

        using var response = await client.PostAsync(
            "/Reviews?handler=Claim",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.ReviewAssignments.AnyAsync(
            assignment => assignment.ReviewerId == reviewerId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_ReviewsPage_ShowsCompletedAndExpiredAssignmentHistory()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var reviewerId = await FindUserIdAsync(email);
        const string CompletedGameTitle = "Completed history game";
        const string ExpiredGameTitle = "Expired history game";

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            var requester = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 2);
            var completedGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            completedGame.Title = CompletedGameTitle;
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                completedGame.Id);
            var expiredGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            expiredGame.Title = ExpiredGameTitle;
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                expiredGame.Id);
        }

        long completedAssignmentId;
        await using (var claimDb = _fixture.NewDbContext())
        {
            var result = await new ReviewClaimService(
                claimDb,
                NullLogger<ReviewClaimService>.Instance)
                .ClaimNextAsync(reviewerId, Guid.NewGuid());
            Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
            completedAssignmentId = await claimDb.ReviewAssignments
                .Where(assignment => assignment.RequestId == result.RequestId)
                .Select(assignment => assignment.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        await using (var submissionDb = _fixture.NewDbContext())
        {
            var result = await new FeedbackSubmissionService(
                submissionDb,
                NullLogger<FeedbackSubmissionService>.Instance)
                .SubmitAsync(
                    completedAssignmentId,
                    reviewerId,
                    "This completed history feedback is intentionally long enough.");
            Assert.Equal(FeedbackSubmissionOutcome.Submitted, result.Outcome);
        }

        long expiredAssignmentId;
        await using (var claimDb = _fixture.NewDbContext())
        {
            var result = await new ReviewClaimService(
                claimDb,
                NullLogger<ReviewClaimService>.Instance)
                .ClaimNextAsync(reviewerId, Guid.NewGuid());
            Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome);
            expiredAssignmentId = await claimDb.ReviewAssignments
                .Where(assignment => assignment.RequestId == result.RequestId)
                .Select(assignment => assignment.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            await claimDb.ReviewAssignments
                .Where(assignment => assignment.Id == expiredAssignmentId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        assignment => assignment.LeasedUntil,
                        DateTimeOffset.UtcNow.AddMinutes(-1)),
                    TestContext.Current.CancellationToken);
        }

        await using (var sweepDb = _fixture.NewDbContext())
        {
            var result = await new ReviewLeaseSweepService(
                sweepDb,
                NullLogger<ReviewLeaseSweepService>.Instance).SweepAsync();
            Assert.Equal(1, result.ExpiredCount);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var response = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("내 작업 이력 2개", html);
        Assert.Contains(CompletedGameTitle, html);
        Assert.Contains(ExpiredGameTitle, html);
        Assert.Contains("완료", html);
        Assert.Contains("만료", html);
        Assert.True(
            html.IndexOf(ExpiredGameTitle, StringComparison.Ordinal)
            < html.IndexOf(CompletedGameTitle, StringComparison.Ordinal));
    }

    private async Task<long> FindUserIdAsync(string email)
    {
        await using var db = _fixture.NewDbContext();
        return (await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken)).Id;
    }

    private async Task AddProfileAsync(long userId, string nickname)
    {
        await using var db = _fixture.NewDbContext();
        var changedAt = DateTimeOffset.UtcNow.AddDays(-8);
        db.UserProfiles.Add(new UserProfile
        {
            UserId = userId,
            Nickname = nickname,
            NormalizedNickname = nickname.ToUpperInvariant(),
            NicknameChangedAt = changedAt,
            CreatedAt = changedAt,
            UpdatedAt = changedAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ReviewRequestPageData> CreateReviewRequestAsync(
        long? existingRequesterId,
        string titlePrefix)
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var requesterEmail = $"{Guid.NewGuid():N}@example.test";
        var requesterId = existingRequesterId
            ?? await CreateRequesterAsync(db, requesterEmail, cancellationToken);

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
            CreationOperationId = Guid.NewGuid(),
            SpendEntry = spendEntry,
        };

        db.CreditLedgerEntries.Add(spendEntry);
        db.ReviewRequests.Add(reviewRequest);
        await db.SaveChangesAsync(cancellationToken);

        return new ReviewRequestPageData(
            reviewRequest.Id,
            game.Title,
            requesterId,
            requesterEmail);
    }

    private static async Task<long> CreateRequesterAsync(
        AppDbContext db,
        string email,
        CancellationToken cancellationToken)
    {
        var requester = TestAccountProtection.AddUser(db, email);

        // Saving here is what assigns the identity value the callers need.
        await db.SaveChangesAsync(cancellationToken);

        return requester.Id;
    }

    private sealed record ReviewRequestPageData(
        long RequestId,
        string GameTitle,
        long RequesterId,
        string RequesterEmail);
}
