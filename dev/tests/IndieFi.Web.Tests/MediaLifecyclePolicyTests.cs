using IndieFi.Web.Modules.Media.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MediaLifecyclePolicyTests
{
    public static TheoryData<MediaStatus, MediaStatus> AllowedTransitions => new()
    {
        { MediaStatus.UploadPending, MediaStatus.ProcessingPending },
        { MediaStatus.UploadPending, MediaStatus.Failed },
        { MediaStatus.UploadPending, MediaStatus.Expired },
        { MediaStatus.UploadPending, MediaStatus.DeletePending },
        { MediaStatus.ProcessingPending, MediaStatus.Processing },
        { MediaStatus.ProcessingPending, MediaStatus.Failed },
        { MediaStatus.ProcessingPending, MediaStatus.DeletePending },
        { MediaStatus.Processing, MediaStatus.ProcessingPending },
        { MediaStatus.Processing, MediaStatus.Ready },
        { MediaStatus.Processing, MediaStatus.Failed },
        { MediaStatus.Processing, MediaStatus.DeletePending },
        { MediaStatus.Ready, MediaStatus.DeletePending },
        { MediaStatus.DeletePending, MediaStatus.Deleted },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void CanTransition_AllowsOnlyDefinedLifecycleEdges(
        MediaStatus current,
        MediaStatus next)
    {
        Assert.True(MediaLifecyclePolicy.CanTransition(current, next));
    }

    [Fact]
    public void CanTransition_RejectsSelfReverseAndTerminalTransitions()
    {
        Assert.False(MediaLifecyclePolicy.CanTransition(MediaStatus.Ready, MediaStatus.Ready));
        Assert.False(MediaLifecyclePolicy.CanTransition(MediaStatus.Ready, MediaStatus.Processing));
        Assert.False(MediaLifecyclePolicy.CanTransition(MediaStatus.Failed, MediaStatus.Ready));
        Assert.False(MediaLifecyclePolicy.CanTransition(MediaStatus.Expired, MediaStatus.UploadPending));
        Assert.False(MediaLifecyclePolicy.CanTransition(MediaStatus.Deleted, MediaStatus.Ready));
    }

    [Fact]
    public void PublicAndTerminalStates_AreNarrow()
    {
        foreach (var status in Enum.GetValues<MediaStatus>())
        {
            Assert.Equal(status == MediaStatus.Ready, MediaLifecyclePolicy.CanBePublic(status));
            Assert.Equal(
                status is MediaStatus.Failed or MediaStatus.Expired or MediaStatus.Deleted,
                MediaLifecyclePolicy.IsTerminal(status));
        }
    }

    [Fact]
    public void UploadPending_ExpiresAtTheTwentyFourHourBoundary()
    {
        var createdAt = new DateTimeOffset(2026, 8, 16, 1, 2, 3, TimeSpan.Zero);

        Assert.False(MediaLifecyclePolicy.IsUploadExpired(
            createdAt,
            createdAt + TimeSpan.FromHours(24) - TimeSpan.FromTicks(1)));
        Assert.True(MediaLifecyclePolicy.IsUploadExpired(
            createdAt,
            createdAt + TimeSpan.FromHours(24)));
        Assert.Equal(TimeSpan.FromHours(24), MediaLifecyclePolicy.UploadPendingLifetime);
        Assert.Equal(TimeSpan.FromMinutes(10), MediaLifecyclePolicy.ProcessingLeaseLifetime);
        Assert.Equal(TimeSpan.FromDays(2), MediaLifecyclePolicy.StagingSafetyLifetime);
        Assert.Equal(TimeSpan.FromDays(30), MediaLifecyclePolicy.TombstoneRetention);
    }

    [Fact]
    public void ProcessingRetry_UsesFiveDelaysThenStopsAfterSixAttempts()
    {
        var expectedDelays = new[]
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(6),
        };

        for (var attempt = 1; attempt <= expectedDelays.Length; attempt++)
        {
            Assert.True(MediaLifecyclePolicy.TryGetProcessingRetryDelay(
                attempt,
                out var retryDelay));
            Assert.Equal(expectedDelays[attempt - 1], retryDelay);
        }

        Assert.False(MediaLifecyclePolicy.TryGetProcessingRetryDelay(6, out _));
        Assert.Equal(6, MediaLifecyclePolicy.MaximumProcessingAttempts);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MediaLifecyclePolicy.TryGetProcessingRetryDelay(0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MediaLifecyclePolicy.TryGetProcessingRetryDelay(7, out _));
    }
}
