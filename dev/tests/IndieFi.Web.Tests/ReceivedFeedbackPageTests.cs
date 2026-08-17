using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
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
    public async Task Get_FeedbackPage_AsOwner_ShowsReviewerNicknameAndProfileLinkWithoutEmail()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var feedbackBody = $"소유자에게 보이는 충분히 긴 피드백 본문입니다. 식별자 {Guid.NewGuid():N}";
        var feedback = await CreateGameWithFeedbackAsync(ownerId, feedbackBody);
        var reviewerNickname = $"Reviewer{Guid.NewGuid():N}"[..14];
        await AddProfileAsync(feedback.ReviewerId, reviewerNickname);

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
        Assert.Contains(reviewerNickname, decodedHtml);
        Assert.Contains($"href=\"/Profiles/{feedback.ReviewerId}\"", decodedHtml);
        Assert.DoesNotContain(
            UserDisplayIdentifier.Format(feedback.ReviewerId),
            decodedHtml);
        Assert.Contains("· 제출 ", decodedHtml);
        Assert.DoesNotContain(feedback.ReviewerEmail, decodedHtml);
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

    [Fact]
    public async Task Get_FeedbackPage_WithUnratedFeedback_ShowsQualityRatingForm()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var feedback = await CreateGameWithFeedbackAsync(
            ownerId,
            $"평가 화면을 확인하기 위한 충분히 긴 피드백입니다. {Guid.NewGuid():N}");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        using var response = await client.GetAsync(
            $"/Games/Feedback/{feedback.GameId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"data-feedback-rating-form=\"{feedback.FeedbackId}\"", html);
        Assert.Contains($"name=\"feedbackId\" value=\"{feedback.FeedbackId}\"", html);
        Assert.Contains("name=\"score\"", html);
        Assert.Contains($"maxlength=\"{FeedbackQualityPolicy.MaximumReasonLength}\"", html);
        Assert.Contains("매우 도움 안 됨", html);
        Assert.Contains("매우 도움 됨", html);
        Assert.Contains("평가 가능 기한", html);
        Assert.Contains("평가 등록", html);
    }

    [Fact]
    public async Task Post_Rating_CreatesThenUpdatesRatingAndShowsCurrentValues()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var feedback = await CreateGameWithFeedbackAsync(
            ownerId,
            $"등록과 수정을 확인하기 위한 충분히 긴 피드백입니다. {Guid.NewGuid():N}");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);

        var initialHtml = await GetFeedbackHtmlAsync(client, feedback.GameId);
        using (var created = await PostRatingAsync(
                   client,
                   feedback.GameId,
                   PageTestSupport.ExtractAntiforgeryToken(initialHtml),
                   feedback.FeedbackId,
                   score: 5,
                   reason: "  실제로 도움이 된 이유  ",
                   revision: null))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            Assert.Contains("rating=saved", created.Headers.Location?.OriginalString);
        }

        var createdHtml = await GetFeedbackHtmlAsync(client, feedback.GameId);
        Assert.Contains("평가를 저장했습니다.", WebUtility.HtmlDecode(createdHtml));
        Assert.Contains("현재 평가:", WebUtility.HtmlDecode(createdHtml));
        Assert.Contains(">5점</strong>", WebUtility.HtmlDecode(createdHtml));
        Assert.Contains("실제로 도움이 된 이유", WebUtility.HtmlDecode(createdHtml));
        Assert.Matches(
            "<option value=\"5\" selected(?:=\"selected\")?>5점",
            WebUtility.HtmlDecode(createdHtml));

        using (var updated = await PostRatingAsync(
                   client,
                   feedback.GameId,
                   PageTestSupport.ExtractAntiforgeryToken(createdHtml),
                   feedback.FeedbackId,
                   score: 2,
                   reason: "수정한 평가 사유",
                   revision: 1))
        {
            Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.FeedbackRatings
            .AsNoTracking()
            .SingleAsync(
                rating => rating.FeedbackSubmissionId == feedback.FeedbackId,
                TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Score);
        Assert.Equal("수정한 평가 사유", stored.Reason);
        Assert.Equal(2, stored.Revision);
    }

    [Fact]
    public async Task Post_Rating_WithInvalidInput_ShowsValidationAndStoresNothing()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var feedback = await CreateGameWithFeedbackAsync(
            ownerId,
            $"검증 오류를 확인하기 위한 충분히 긴 피드백입니다. {Guid.NewGuid():N}");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        var pageHtml = await GetFeedbackHtmlAsync(client, feedback.GameId);

        using var response = await PostRatingAsync(
            client,
            feedback.GameId,
            PageTestSupport.ExtractAntiforgeryToken(pageHtml),
            feedback.FeedbackId,
            score: 6,
            reason: new string('가', FeedbackQualityPolicy.MaximumReasonLength + 1),
            revision: null);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("평가는 1점부터 5점까지 선택해 주세요.", html);
        Assert.Contains("평가 사유는 500자 이하여야 합니다.", html);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.FeedbackRatings.AnyAsync(
            rating => rating.FeedbackSubmissionId == feedback.FeedbackId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FeedbackPage_WhenRatingWindowIsClosed_ShowsReadOnlyStateAndRejectsPost()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var feedback = await CreateGameWithFeedbackAsync(
            ownerId,
            $"평가 기한을 확인하기 위한 충분히 긴 피드백입니다. {Guid.NewGuid():N}");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        var openPageHtml = await GetFeedbackHtmlAsync(client, feedback.GameId);
        using (var created = await PostRatingAsync(
                   client,
                   feedback.GameId,
                   PageTestSupport.ExtractAntiforgeryToken(openPageHtml),
                   feedback.FeedbackId,
                   score: 4,
                   reason: "기한 종료 뒤에도 보이는 평가 사유",
                   revision: null))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        }

        await using (var expirationDb = _fixture.NewOwnerDbContext())
        {
            await expirationDb.FeedbackSubmissions
                .Where(submission => submission.Id == feedback.FeedbackId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        submission => submission.CreatedAt,
                        DateTimeOffset.UtcNow.AddDays(-31)),
                    TestContext.Current.CancellationToken);
        }
        var pageHtml = await GetFeedbackHtmlAsync(client, feedback.GameId);
        var decodedPageHtml = WebUtility.HtmlDecode(pageHtml);

        Assert.Contains("평가 기간이 종료되었습니다.", decodedPageHtml);
        Assert.Contains(">4점</strong>", decodedPageHtml);
        Assert.Contains("기한 종료 뒤에도 보이는 평가 사유", decodedPageHtml);
        Assert.DoesNotContain(
            $"data-feedback-rating-form=\"{feedback.FeedbackId}\"",
            decodedPageHtml);

        using var response = await PostRatingAsync(
            client,
            feedback.GameId,
            PageTestSupport.ExtractAntiforgeryToken(pageHtml),
            feedback.FeedbackId,
            score: 5,
            reason: null,
            revision: 1);
        var postHtml = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("평가 기간이 종료되어 저장할 수 없습니다.", postHtml);
        await using var verificationDb = _fixture.NewDbContext();
        var unchangedScore = await verificationDb.FeedbackRatings
            .Where(rating => rating.FeedbackSubmissionId == feedback.FeedbackId)
            .Select(rating => rating.Score)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, unchangedScore);
    }

    [Fact]
    public async Task Post_Rating_WithStaleRevision_ShowsConflictAndKeepsCurrentRating()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var ownerId = await FindUserIdAsync(email);
        var feedback = await CreateGameWithFeedbackAsync(
            ownerId,
            $"평가 충돌을 확인하기 위한 충분히 긴 피드백입니다. {Guid.NewGuid():N}");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        var initialHtml = await GetFeedbackHtmlAsync(client, feedback.GameId);
        using (var created = await PostRatingAsync(
                   client,
                   feedback.GameId,
                   PageTestSupport.ExtractAntiforgeryToken(initialHtml),
                   feedback.FeedbackId,
                   score: 3,
                   reason: "최초 평가",
                   revision: null))
        {
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        }

        await using (var concurrentDb = _fixture.NewOwnerDbContext())
        {
            await concurrentDb.FeedbackRatings
                .Where(rating => rating.FeedbackSubmissionId == feedback.FeedbackId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(rating => rating.Score, 4)
                        .SetProperty(rating => rating.Reason, "다른 요청의 평가")
                        .SetProperty(rating => rating.Revision, 2),
                    TestContext.Current.CancellationToken);
        }

        var latestPageHtml = await GetFeedbackHtmlAsync(client, feedback.GameId);
        using var response = await PostRatingAsync(
            client,
            feedback.GameId,
            PageTestSupport.ExtractAntiforgeryToken(latestPageHtml),
            feedback.FeedbackId,
            score: 5,
            reason: "뒤늦게 도착한 평가",
            revision: 1);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("다른 요청에서 평가가 변경되었습니다.", html);
        Assert.Contains(">4점</strong>", html);
        Assert.Contains("다른 요청의 평가", html);
        Assert.DoesNotContain("뒤늦게 도착한 평가", html);
    }

    [Fact]
    public async Task Post_Rating_ForFeedbackOutsideOwnedGame_ReturnsNotFound()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        var userId = await FindUserIdAsync(email);
        long ownedGameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownedGameId = (await ReviewTestData.CreateGameAsync(arrangeDb, userId)).Id;
        }

        long otherOwnerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            otherOwnerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        var otherFeedback = await CreateGameWithFeedbackAsync(
            otherOwnerId,
            $"다른 소유자의 게임에 속한 충분히 긴 피드백입니다. {Guid.NewGuid():N}");

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        var ownedPageHtml = await GetFeedbackHtmlAsync(client, ownedGameId);

        using var response = await PostRatingAsync(
            client,
            ownedGameId,
            PageTestSupport.ExtractAntiforgeryToken(ownedPageHtml),
            otherFeedback.FeedbackId,
            score: 4,
            reason: null,
            revision: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        var reviewerEmail = $"{Guid.NewGuid():N}@example.test";
        var reviewer = await ReviewTestData.CreateUserWithCreditAsync(
            db,
            balance: 0,
            reviewerEmail);
        var game = await ReviewTestData.CreateGameAsync(db, ownerId);
        var reviewRequest = await ReviewTestData.CreateQueuedRequestAsync(
            db,
            ownerId,
            game.Id);

        var completedAt = DateTimeOffset.UtcNow;
        reviewRequest.Status = ReviewRequestStatus.Completed;
        reviewRequest.CompletedAt = completedAt;
        var assignment = new ReviewAssignment
        {
            RequestId = reviewRequest.Id,
            GameId = game.Id,
            ReviewerId = reviewer.Id,
            ClaimOperationId = Guid.NewGuid(),
            Status = ReviewAssignmentStatus.Completed,
            LeasedUntil = DateTimeOffset.UtcNow.AddHours(1),
            CompletedAt = completedAt,
        };
        db.ReviewAssignments.Add(assignment);
        await db.SaveChangesAsync(cancellationToken);

        var earnEntry = new CreditLedgerEntry
        {
            UserId = reviewer.Id,
            Kind = CreditEntryKind.Earn,
            Delta = 1,
        };

        // The read page does not need queue arbitration, so inserting the final row directly
        // keeps setup deterministic while preserving every database relationship and constraint.
        var submission = new FeedbackSubmission
        {
            AssignmentId = assignment.Id,
            ReviewerId = reviewer.Id,
            EarnEntry = earnEntry,
            Body = body,
        };
        db.FeedbackSubmissions.Add(submission);
        db.CreditLedgerEntries.Add(earnEntry);
        await db.SaveChangesAsync(cancellationToken);

        return new FeedbackTestData(
            game.Id,
            game.Title,
            submission.Id,
            reviewer.Id,
            reviewerEmail);
    }

    private static async Task<string> GetFeedbackHtmlAsync(
        HttpClient client,
        long gameId)
    {
        using var response = await client.GetAsync(
            $"/Games/Feedback/{gameId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostRatingAsync(
        HttpClient client,
        long gameId,
        string antiforgeryToken,
        long feedbackId,
        int score,
        string? reason,
        int? revision)
    {
        var values = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["feedbackId"] = feedbackId.ToString(),
            ["score"] = score.ToString(),
            ["reason"] = reason ?? string.Empty,
        };
        if (revision is int expectedRevision)
        {
            values["revision"] = expectedRevision.ToString();
        }

        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(
            $"/Games/Feedback/{gameId}?handler=Rate",
            form,
            TestContext.Current.CancellationToken);
    }

    private sealed record FeedbackTestData(
        long GameId,
        string GameTitle,
        long FeedbackId,
        long ReviewerId,
        string ReviewerEmail);
}
