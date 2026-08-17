using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(RedisCollection.Name)]
public sealed class MediaUploadIssueRateLimiterTests
{
    private static long _nextOwnerId = 10_000;
    private readonly RedisFixture _fixture;

    public MediaUploadIssueRateLimiterTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void OptionsAndKeys_RequireSafePrefixAndIsolateAccounts()
    {
        Assert.True(MediaUploadRateLimitOptions.IsValid(new()));
        Assert.False(MediaUploadRateLimitOptions.IsValid(new()
        {
            KeyPrefix = "unsafe prefix",
        }));

        var keys = new MediaUploadRateLimitKeys(
            Options.Create(new MediaUploadRateLimitOptions
            {
                KeyPrefix = "test:media-upload:v1",
            }));

        Assert.Equal(
            "test:media-upload:v1:{account:11}:issues",
            keys.Issues(11).ToString());
        Assert.NotEqual(keys.Issues(11), keys.Issues(12));
    }

    [Fact]
    public async Task TryAcquireAsync_ConcurrentRequests_AtomicallyAllowsTwenty()
    {
        var ownerId = NextOwnerId();
        var limiter = NewLimiter();

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            limiter.TryAcquireAsync(
                ownerId,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken)));

        Assert.Equal(
            MediaPolicy.IssueLimitPerShortWindow,
            results.Count(item => item == MediaUploadIssueRateLimitOutcome.Allowed));
        Assert.Equal(
            20,
            results.Count(item =>
                item == MediaUploadIssueRateLimitOutcome.ShortWindowLimitReached));
        Assert.DoesNotContain(
            MediaUploadIssueRateLimitOutcome.DailyLimitReached,
            results);
    }

    [Fact]
    public async Task TryAcquireAsync_WithOneHundredDailyEvents_RejectsDailyLimit()
    {
        var ownerId = NextOwnerId();
        var prefix = Prefix();
        var options = Options.Create(new MediaUploadRateLimitOptions
        {
            KeyPrefix = prefix,
        });
        var keys = new MediaUploadRateLimitKeys(options);
        var database = await _fixture.GetDatabaseAsync();
        var olderThanShortWindow = DateTimeOffset.UtcNow
            .Subtract(MediaPolicy.IssueShortWindow)
            .AddMinutes(-1)
            .ToUnixTimeMilliseconds();
        var entries = Enumerable.Range(0, MediaPolicy.IssueLimitPerDay)
            .Select(_ => new SortedSetEntry(
                Guid.NewGuid().ToString("N"),
                olderThanShortWindow))
            .ToArray();
        await database.SortedSetAddAsync(keys.Issues(ownerId), entries);

        var outcome = await NewLimiter(keys).TryAcquireAsync(
            ownerId,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadIssueRateLimitOutcome.DailyLimitReached, outcome);
        Assert.Equal(
            MediaPolicy.IssueLimitPerDay,
            await database.SortedSetLengthAsync(keys.Issues(ownerId)));
    }

    [Fact]
    public async Task TryAcquireAsync_DifferentAccounts_HaveIndependentLimits()
    {
        var firstOwnerId = NextOwnerId();
        var secondOwnerId = NextOwnerId();
        var limiter = NewLimiter();

        for (var index = 0; index < MediaPolicy.IssueLimitPerShortWindow; index++)
        {
            Assert.Equal(
                MediaUploadIssueRateLimitOutcome.Allowed,
                await limiter.TryAcquireAsync(
                    firstOwnerId,
                    Guid.NewGuid(),
                    TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            MediaUploadIssueRateLimitOutcome.ShortWindowLimitReached,
            await limiter.TryAcquireAsync(
                firstOwnerId,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            MediaUploadIssueRateLimitOutcome.Allowed,
            await limiter.TryAcquireAsync(
                secondOwnerId,
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryAcquireAsync_WithSameIssueId_IsIdempotent()
    {
        var ownerId = NextOwnerId();
        var issueId = Guid.NewGuid();
        var options = Options.Create(new MediaUploadRateLimitOptions
        {
            KeyPrefix = Prefix(),
        });
        var keys = new MediaUploadRateLimitKeys(options);
        var limiter = NewLimiter(keys);

        var first = await limiter.TryAcquireAsync(
            ownerId,
            issueId,
            TestContext.Current.CancellationToken);
        var repeated = await limiter.TryAcquireAsync(
            ownerId,
            issueId,
            TestContext.Current.CancellationToken);

        Assert.Equal(MediaUploadIssueRateLimitOutcome.Allowed, first);
        Assert.Equal(MediaUploadIssueRateLimitOutcome.Allowed, repeated);
        Assert.Equal(
            1,
            await (await _fixture.GetDatabaseAsync()).SortedSetLengthAsync(
                keys.Issues(ownerId)));
    }

    [Fact]
    public async Task TryAcquireAsync_WhenRedisIsDisabled_FailsClosed()
    {
        await using var connection = new WaitingRoomRedisConnection(
            Options.Create(new WaitingRoomRedisOptions { Enabled = false }));
        var options = Options.Create(new MediaUploadRateLimitOptions());
        var limiter = new RedisMediaUploadIssueRateLimiter(
            connection,
            new MediaUploadRateLimitKeys(options));

        await Assert.ThrowsAsync<MediaUploadIssueRateLimitUnavailableException>(() =>
            limiter.TryAcquireAsync(
                NextOwnerId(),
                Guid.NewGuid(),
                TestContext.Current.CancellationToken));
    }

    private RedisMediaUploadIssueRateLimiter NewLimiter()
    {
        var options = Options.Create(new MediaUploadRateLimitOptions
        {
            KeyPrefix = Prefix(),
        });
        return NewLimiter(new MediaUploadRateLimitKeys(options));
    }

    private RedisMediaUploadIssueRateLimiter NewLimiter(
        MediaUploadRateLimitKeys keys) =>
        new(_fixture.GetConnection(), keys);

    private static long NextOwnerId() => Interlocked.Increment(ref _nextOwnerId);

    private static string Prefix() => $"test:media-upload:{Guid.NewGuid():N}";
}
