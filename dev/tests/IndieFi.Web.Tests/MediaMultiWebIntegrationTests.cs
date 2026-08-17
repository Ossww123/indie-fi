using System.Net;
using System.Text.RegularExpressions;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed partial class MediaMultiWebIntegrationTests :
    IClassFixture<RedisFixture>
{
    private readonly PostgreSqlFixture _postgres;
    private readonly RedisFixture _redis;

    public MediaMultiWebIntegrationTests(
        PostgreSqlFixture postgres,
        RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    [Fact]
    public async Task ProductionWebs_SharingKeyRing_AcceptDiscoveryCursorAcrossInstances()
    {
        var token = $"multi-web-{Guid.NewGuid():N}";
        await using (var db = _postgres.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
            db.Games.AddRange(Enumerable.Range(0, 25).Select(index => new Game
            {
                OwnerId = owner.Id,
                Title = $"{token} {index:D2}",
            }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var baseFactoryA = new WebAppFactory(_postgres.ConnectionString);
        using var factoryA = ProductionFactory(baseFactoryA);
        using var clientA = HttpsClient(factoryA);
        using var firstResponse = await clientA.GetAsync(
            "/?q=" + Uri.EscapeDataString(token),
            TestContext.Current.CancellationToken);
        var firstHtml = WebUtility.HtmlDecode(
            await firstResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        var nextPath = NextLinkPattern().Match(firstHtml).Groups[1].Value;

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(24, CountOccurrences(firstHtml, "data-discovery-result"));
        Assert.NotEmpty(nextPath);

        using var baseFactoryB = new WebAppFactory(_postgres.ConnectionString);
        using var factoryB = ProductionFactory(baseFactoryB);
        using var clientB = HttpsClient(factoryB);
        using var secondResponse = await clientB.GetAsync(
            nextPath,
            TestContext.Current.CancellationToken);
        var secondHtml = await secondResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(1, CountOccurrences(secondHtml, "data-discovery-result"));
    }

    [Fact]
    public async Task Webs_SharingRedis_EnforceOneUploadIssueLimit()
    {
        var prefix = $"test:media-multi-web:{Guid.NewGuid():N}";
        using var baseFactoryA = new WebAppFactory(_postgres.ConnectionString);
        using var factoryA = RedisFactory(baseFactoryA, prefix);
        using var baseFactoryB = new WebAppFactory(_postgres.ConnectionString);
        using var factoryB = RedisFactory(baseFactoryB, prefix);
        var firstLimiter = factoryA.Services
            .GetRequiredService<IMediaUploadIssueRateLimiter>();
        var secondLimiter = factoryB.Services
            .GetRequiredService<IMediaUploadIssueRateLimiter>();
        const long ownerId = 987_654_321;

        var accepted = new List<MediaUploadIssueRateLimitOutcome>();
        for (var index = 0; index < MediaPolicy.IssueLimitPerShortWindow; index++)
        {
            var limiter = index % 2 == 0 ? firstLimiter : secondLimiter;
            accepted.Add(await limiter.TryAcquireAsync(
                ownerId,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));
        }

        var rejected = await secondLimiter.TryAcquireAsync(
            ownerId,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.All(
            accepted,
            outcome => Assert.Equal(MediaUploadIssueRateLimitOutcome.Allowed, outcome));
        Assert.Equal(
            MediaUploadIssueRateLimitOutcome.ShortWindowLimitReached,
            rejected);
    }

    [Fact]
    public async Task Webs_SharingDatabase_ObserveOneMediaReservationState()
    {
        long ownerId;
        long gameId;
        await using (var db = _postgres.NewDbContext())
        {
            var owner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
            var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
            ownerId = owner.Id;
            gameId = game.Id;
        }

        using var baseFactoryA = new WebAppFactory(_postgres.ConnectionString);
        using var factoryA = ProductionFactory(baseFactoryA);
        using var baseFactoryB = new WebAppFactory(_postgres.ConnectionString);
        using var factoryB = ProductionFactory(baseFactoryB);
        await using var firstScope = factoryA.Services.CreateAsyncScope();
        await using var secondScope = factoryB.Services.CreateAsyncScope();

        var first = await firstScope.ServiceProvider
            .GetRequiredService<MediaUploadReservationService>()
            .ReserveAsync(
                ownerId,
                MediaTarget.Game(gameId),
                MediaAttachmentKind.GameCover,
                "image/png",
                declaredBytes: 1024,
                TestContext.Current.CancellationToken);
        var second = await secondScope.ServiceProvider
            .GetRequiredService<MediaUploadReservationService>()
            .ReserveAsync(
                ownerId,
                MediaTarget.Game(gameId),
                MediaAttachmentKind.GameCover,
                "image/png",
                declaredBytes: 1024,
                TestContext.Current.CancellationToken);
        var observed = await secondScope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .MediaAssets
            .AsNoTracking()
            .SingleAsync(
                media => media.Id == first.MediaId,
                TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadReservationOutcome.Reserved, first.Outcome);
        Assert.Equal(
            MediaUploadReservationOutcome.ReplacementInProgress,
            second.Outcome);
        Assert.Equal(MediaStatus.UploadPending, observed.Status);
        Assert.Equal(gameId, observed.GameId);
    }

    private WebApplicationFactory<Program> RedisFactory(
        WebAppFactory factory,
        string mediaKeyPrefix) =>
        ProductionFactory(factory, builder =>
        {
            builder.UseSetting("WaitingRoom:Redis:Enabled", "true");
            builder.UseSetting(
                "WaitingRoom:Redis:ConnectionString",
                _redis.ConnectionString);
            builder.UseSetting("WaitingRoom:Redis:ConnectTimeout", "00:00:05");
            builder.UseSetting("Media:UploadRateLimit:KeyPrefix", mediaKeyPrefix);
        });

    private static WebApplicationFactory<Program> ProductionFactory(
        WebAppFactory factory,
        Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            configure?.Invoke(builder);
        });

    private static HttpClient HttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var position = 0;
        while ((position = source.IndexOf(
                   value,
                   position,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += value.Length;
        }

        return count;
    }

    [GeneratedRegex("<a[^>]*data-discovery-next[^>]*href=\"([^\"]+)\"")]
    private static partial Regex NextLinkPattern();
}
