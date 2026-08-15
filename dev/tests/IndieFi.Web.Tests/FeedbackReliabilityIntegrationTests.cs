using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FeedbackReliabilityIntegrationTests
{
    private readonly PostgreSqlFixture _fixture;

    public FeedbackReliabilityIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentRequestAsync_WithSameOperation_CreatesOneRequestAndSpend()
    {
        long requesterId;
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            var requester = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 2);
            requesterId = requester.Id;
            gameId = (await ReviewTestData.CreateGameAsync(arrangeDb, requesterId)).Id;
        }

        var operationId = Guid.NewGuid();
        var results = await Task.WhenAll(
            RequestAsync(gameId, requesterId, operationId),
            RequestAsync(gameId, requesterId, operationId));

        Assert.All(results, result => Assert.Equal(ReviewRequestOutcome.Queued, result.Outcome));
        Assert.Single(results.Select(result => result.RequestId).Distinct());
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.ReviewRequests.CountAsync(
            request => request.RequesterId == requesterId
                && request.CreationOperationId == operationId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == requesterId && entry.Kind == CreditEntryKind.Spend,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.Users
            .Where(user => user.Id == requesterId)
            .Select(user => user.CreditBalance)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentClaimAsync_WithSameOperation_CreatesOneAssignment()
    {
        long reviewerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            var requester = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 2);
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
            var firstGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            var secondGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                firstGame.Id);
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                secondGame.Id);
        }

        var operationId = Guid.NewGuid();
        var results = await Task.WhenAll(
            ClaimAsync(reviewerId, operationId),
            ClaimAsync(reviewerId, operationId));

        Assert.All(results, result => Assert.Equal(ReviewClaimOutcome.Claimed, result.Outcome));
        Assert.Single(results.Select(result => result.RequestId).Distinct());
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.ReviewAssignments.CountAsync(
            assignment => assignment.ReviewerId == reviewerId
                && assignment.ClaimOperationId == operationId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.ReviewRequests.CountAsync(
            request => request.Status == ReviewRequestStatus.Assigned,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.ReviewRequests.CountAsync(
            request => request.Status == ReviewRequestStatus.Queued,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HttpRequestFeedback_WhenSameFormIsRetried_CreatesOneRequest()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        long requesterId;
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            requesterId = (await TestAccountProtection.FindUserAsync(
                arrangeDb,
                email,
                TestContext.Current.CancellationToken)).Id;
            arrangeDb.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                UserId = requesterId,
                Kind = CreditEntryKind.Grant,
                Delta = 2,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = (await ReviewTestData.CreateGameAsync(arrangeDb, requesterId)).Id;
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var pageResponse = await client.GetAsync(
            $"/Games/Detail/{gameId}",
            TestContext.Current.CancellationToken);
        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(html);
        var operationId = Guid.Parse(PageTestSupport.ExtractHiddenInput(html, "operationId"));

        using var firstForm = RequestForm(antiforgeryToken, operationId);
        using var firstResponse = await client.PostAsync(
            $"/Games/Detail/{gameId}?handler=RequestFeedback",
            firstForm,
            TestContext.Current.CancellationToken);
        using var retryForm = RequestForm(antiforgeryToken, operationId);
        using var retryResponse = await client.PostAsync(
            $"/Games/Detail/{gameId}?handler=RequestFeedback",
            retryForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, retryResponse.StatusCode);
        Assert.Contains("request=queued", firstResponse.Headers.Location?.OriginalString);
        Assert.Contains("request=queued", retryResponse.Headers.Location?.OriginalString);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.ReviewRequests.CountAsync(
            request => request.RequesterId == requesterId
                && request.CreationOperationId == operationId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.CreditLedgerEntries.CountAsync(
            entry => entry.UserId == requesterId && entry.Kind == CreditEntryKind.Spend,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HttpClaim_WhenSameFormIsRetried_CreatesOneAssignment()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(_fixture);
        long reviewerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            await arrangeDb.ReviewRequests.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            reviewerId = (await TestAccountProtection.FindUserAsync(
                arrangeDb,
                email,
                TestContext.Current.CancellationToken)).Id;
            var requester = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 2);
            var firstGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            var secondGame = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                firstGame.Id);
            await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                secondGame.Id);
        }

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var pageResponse = await client.GetAsync(
            "/Reviews",
            TestContext.Current.CancellationToken);
        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var antiforgeryToken = PageTestSupport.ExtractAntiforgeryToken(html);
        var operationId = Guid.Parse(PageTestSupport.ExtractHiddenInput(html, "operationId"));

        using var firstForm = RequestForm(antiforgeryToken, operationId);
        using var firstResponse = await client.PostAsync(
            "/Reviews?handler=Claim",
            firstForm,
            TestContext.Current.CancellationToken);
        using var retryForm = RequestForm(antiforgeryToken, operationId);
        using var retryResponse = await client.PostAsync(
            "/Reviews?handler=Claim",
            retryForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, retryResponse.StatusCode);
        Assert.Contains("claim=claimed", firstResponse.Headers.Location?.OriginalString);
        Assert.Contains("claim=claimed", retryResponse.Headers.Location?.OriginalString);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.ReviewAssignments.CountAsync(
            assignment => assignment.ReviewerId == reviewerId
                && assignment.ClaimOperationId == operationId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.ReviewRequests.CountAsync(
            request => request.Status == ReviewRequestStatus.Queued,
            TestContext.Current.CancellationToken));
    }

    private async Task<ReviewRequestResult> RequestAsync(
        long gameId,
        long requesterId,
        Guid operationId)
    {
        await using var db = _fixture.NewDbContext();
        return await new ReviewRequestService(
            db,
            NullLogger<ReviewRequestService>.Instance)
            .RequestAsync(gameId, requesterId, operationId);
    }

    private async Task<ReviewClaimResult> ClaimAsync(long reviewerId, Guid operationId)
    {
        await using var db = _fixture.NewDbContext();
        return await new ReviewClaimService(
            db,
            NullLogger<ReviewClaimService>.Instance)
            .ClaimNextAsync(reviewerId, operationId);
    }

    private static FormUrlEncodedContent RequestForm(
        string antiforgeryToken,
        Guid operationId)
    {
        return new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["operationId"] = operationId.ToString(),
        });
    }
}
