using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MediaPolicyTests
{
    [Fact]
    public void Targets_UseOnlyPositiveOpaqueDatabaseIdentifiers()
    {
        Assert.Equal(
            new { Kind = MediaTargetKind.Game, Id = 17L },
            new { MediaTarget.Game(17).Kind, MediaTarget.Game(17).Id });
        Assert.Equal(
            new { Kind = MediaTargetKind.Profile, Id = 23L },
            new { MediaTarget.Profile(23).Kind, MediaTarget.Profile(23).Id });

        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTarget.Game(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTarget.Profile(-1));
    }

    [Theory]
    [InlineData(MediaAttachmentKind.GameCover, MediaTargetKind.Game, 1, true)]
    [InlineData(MediaAttachmentKind.GameScreenshot, MediaTargetKind.Game, 8, false)]
    [InlineData(MediaAttachmentKind.ProfileImage, MediaTargetKind.Profile, 1, true)]
    public void AttachmentKinds_DefineTargetCountAndReplacementShape(
        MediaAttachmentKind attachmentKind,
        MediaTargetKind expectedTargetKind,
        int expectedMaximumCount,
        bool expectedSingleSlot)
    {
        Assert.Equal(expectedTargetKind, MediaPolicy.GetTargetKind(attachmentKind));
        Assert.Equal(expectedMaximumCount, MediaPolicy.GetMaximumAttachedCount(attachmentKind));
        Assert.Equal(expectedSingleSlot, MediaPolicy.UsesSingleCurrentSlot(attachmentKind));
    }

    [Fact]
    public void TargetCompatibility_RejectsCrossTargetAttachments()
    {
        MediaPolicy.RequireCompatible(
            MediaTarget.Game(1),
            MediaAttachmentKind.GameScreenshot);
        MediaPolicy.RequireCompatible(
            MediaTarget.Profile(2),
            MediaAttachmentKind.ProfileImage);

        Assert.Throws<ArgumentException>(() => MediaPolicy.RequireCompatible(
            MediaTarget.Game(1),
            MediaAttachmentKind.ProfileImage));
        Assert.Throws<ArgumentException>(() => MediaPolicy.RequireCompatible(
            MediaTarget.Profile(2),
            MediaAttachmentKind.GameCover));
    }

    [Fact]
    public void ImageLimits_MatchTheSpecificationAndSigningBoundary()
    {
        var game = MediaPolicy.GetImageLimits(MediaAttachmentKind.GameCover);
        var screenshot = MediaPolicy.GetImageLimits(MediaAttachmentKind.GameScreenshot);
        var profile = MediaPolicy.GetImageLimits(MediaAttachmentKind.ProfileImage);

        Assert.Same(game, screenshot);
        Assert.Equal(10L * 1024 * 1024, game.MaximumOriginalBytes);
        Assert.Equal(12_000, game.MaximumWidth);
        Assert.Equal(12_000, game.MaximumHeight);
        Assert.Equal(40_000_000, game.MaximumDecodedPixels);
        Assert.Equal(5L * 1024 * 1024, profile.MaximumOriginalBytes);
        Assert.Equal(8_192, profile.MaximumWidth);
        Assert.Equal(8_192, profile.MaximumHeight);
        Assert.Equal(16_000_000, profile.MaximumDecodedPixels);
        Assert.Equal(MediaObjectAccessPolicy.GameImageMaximumBytes, game.MaximumOriginalBytes);
        Assert.Equal(MediaObjectAccessPolicy.ProfileImageMaximumBytes, profile.MaximumOriginalBytes);
    }

    [Fact]
    public void ReservationAndIssueLimits_AreExplicit()
    {
        Assert.Equal(50L * 1024 * 1024, MediaPolicy.GetMaximumActiveOriginalBytes(MediaTargetKind.Game));
        Assert.Equal(5L * 1024 * 1024, MediaPolicy.GetMaximumActiveOriginalBytes(MediaTargetKind.Profile));
        Assert.Equal(10, MediaPolicy.MaximumUploadPendingPerAccount);
        Assert.Equal(1, MediaPolicy.MaximumConcurrentReplacementCandidates);
        Assert.Equal(20, MediaPolicy.IssueLimitPerShortWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), MediaPolicy.IssueShortWindow);
        Assert.Equal(100, MediaPolicy.IssueLimitPerDay);
        Assert.Equal(TimeSpan.FromDays(1), MediaPolicy.IssueDailyWindow);
    }
}
