using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MediaObjectAccessPolicyTests
{
    private static readonly Guid MediaId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid OtherMediaId =
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid UploadAttemptId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public void ObjectKeys_UseCanonicalOpaqueIdentifiersAndFixedVariantNames()
    {
        var staging = MediaObjectKey.Staging(MediaId, UploadAttemptId);
        var source = MediaObjectKey.Source(MediaId, revision: 3);
        var derived = new[]
        {
            MediaObjectKey.Derived(MediaId, 3, MediaDerivedVariant.GameDisplay),
            MediaObjectKey.Derived(MediaId, 3, MediaDerivedVariant.GameList),
            MediaObjectKey.Derived(MediaId, 3, MediaDerivedVariant.ProfileDisplay),
            MediaObjectKey.Derived(MediaId, 3, MediaDerivedVariant.ProfileThumbnail),
        };

        Assert.Equal(
            "staging/11111111111141118111111111111111/22222222222242228222222222222222",
            staging.Value);
        Assert.Equal(
            "sources/11111111111141118111111111111111/3",
            source.Value);
        Assert.Equal(
            [
                "derived/11111111111141118111111111111111/3/game-display.webp",
                "derived/11111111111141118111111111111111/3/game-list.webp",
                "derived/11111111111141118111111111111111/3/profile-display.webp",
                "derived/11111111111141118111111111111111/3/profile-thumbnail.webp",
            ],
            derived.Select(key => key.Value));
        Assert.All([staging, source, .. derived], key =>
        {
            Assert.DoesNotContain('@', key.Value);
            Assert.DoesNotContain(' ', key.Value);
            Assert.DoesNotContain("..", key.Value);
        });
    }

    [Fact]
    public void Parse_RoundTripsEverySupportedObjectKey()
    {
        var keys = new[]
        {
            MediaObjectKey.Staging(MediaId, UploadAttemptId),
            MediaObjectKey.Source(MediaId, 1),
            MediaObjectKey.Derived(MediaId, 1, MediaDerivedVariant.GameDisplay),
            MediaObjectKey.Derived(MediaId, 2, MediaDerivedVariant.GameList),
            MediaObjectKey.Derived(MediaId, 3, MediaDerivedVariant.ProfileDisplay),
            MediaObjectKey.Derived(MediaId, 4, MediaDerivedVariant.ProfileThumbnail),
        };

        foreach (var expected in keys)
        {
            Assert.True(MediaObjectKey.TryParse(expected.Value, out var actual));
            Assert.Equal(expected, actual);
            Assert.Equal(expected, MediaObjectKey.Parse(expected.Value));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("staging/11111111111141118111111111111111")]
    [InlineData("staging/11111111111141118111111111111111/user@example.com")]
    [InlineData("staging/11111111111141118111111111111111/22222222222242228222222222222222/file.png")]
    [InlineData("sources/11111111111141118111111111111111/0")]
    [InlineData("sources/11111111111141118111111111111111/01")]
    [InlineData("sources/11111111111141118111111111111111/-1")]
    [InlineData("derived/11111111111141118111111111111111/1/original.png")]
    [InlineData("derived/11111111111141118111111111111111/1/game-display.PNG")]
    [InlineData("derived/11111111111141118111111111111111/1/unknown.webp")]
    [InlineData("derived/11111111111141118111111111111111/1/../game-display.webp")]
    [InlineData("Derived/11111111111141118111111111111111/1/game-display.webp")]
    [InlineData("derived/AAAAAAAAAAAA4AAA8AAAAAAAAAAAAAAA/1/game-display.webp")]
    public void Parse_RejectsNonCanonicalOrDescriptiveKeys(string value)
    {
        Assert.False(MediaObjectKey.TryParse(value, out _));
        Assert.Throws<FormatException>(() => MediaObjectKey.Parse(value));
    }

    [Fact]
    public void CreateKeys_RejectsEmptyIdentifiersAndNonPositiveRevision()
    {
        Assert.Throws<ArgumentException>(
            () => MediaObjectKey.Staging(Guid.Empty, UploadAttemptId));
        Assert.Throws<ArgumentException>(
            () => MediaObjectKey.Staging(MediaId, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaObjectKey.Source(MediaId, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaObjectKey.Derived(MediaId, -1, MediaDerivedVariant.GameDisplay));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaObjectKey.Derived(MediaId, 1, (MediaDerivedVariant)999));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public void UploadRequest_AllowsOnlyDeclaredStaticImageTypes(string contentType)
    {
        var request = MediaObjectAccessPolicy.CreateUploadUrlRequest(
            Staging(),
            MediaUploadKind.GameImage,
            contentType,
            MediaObjectAccessPolicy.GameImageMaximumBytes);

        Assert.Equal(Staging(), request.ObjectKey);
        Assert.Equal(contentType, request.ContentType);
        Assert.Equal(MediaObjectAccessPolicy.GameImageMaximumBytes, request.ContentLength);
        Assert.Equal(TimeSpan.FromMinutes(15), MediaObjectAccessPolicy.UploadUrlLifetime);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    [InlineData("IMAGE/PNG")]
    [InlineData("image/png; charset=utf-8")]
    [InlineData("")]
    public void UploadRequest_RejectsUnsupportedOrNonCanonicalContentType(string contentType)
    {
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                Staging(),
                MediaUploadKind.GameImage,
                contentType,
                1));
    }

    [Fact]
    public void UploadRequest_UsesPerTargetByteLimitAndStagingOnly()
    {
        Assert.NotNull(MediaObjectAccessPolicy.CreateUploadUrlRequest(
            Staging(),
            MediaUploadKind.ProfileImage,
            "image/png",
            MediaObjectAccessPolicy.ProfileImageMaximumBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                Staging(),
                MediaUploadKind.ProfileImage,
                "image/png",
                MediaObjectAccessPolicy.ProfileImageMaximumBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                Staging(),
                MediaUploadKind.GameImage,
                "image/png",
                0));
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                Source(),
                MediaUploadKind.GameImage,
                "image/png",
                1));
    }

    [Fact]
    public void DownloadRequest_AllowsOnlyDerivedObjectsWithFiveMinuteLifetime()
    {
        var derived = Derived();

        var request = MediaObjectAccessPolicy.CreateDownloadUrlRequest(derived);

        Assert.Equal(derived, request.ObjectKey);
        Assert.Equal(TimeSpan.FromMinutes(5), MediaObjectAccessPolicy.DownloadUrlLifetime);
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateDownloadUrlRequest(Source()));
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateDownloadUrlRequest(Staging()));
    }

    [Fact]
    public void DerivedWrite_AllowsOnlyReadableWebpDerivedContentWithinLimit()
    {
        using var stream = new MemoryStream([1]);

        var request = MediaObjectAccessPolicy.CreateDerivedWriteRequest(
            Derived(),
            stream,
            MediaObjectAccessPolicy.DerivedImageMaximumBytes);

        Assert.Equal("image/webp", request.ContentType);
        Assert.Same(stream, request.Content);
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateDerivedWriteRequest(Source(), stream, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MediaObjectAccessPolicy.CreateDerivedWriteRequest(
                Derived(),
                stream,
                MediaObjectAccessPolicy.DerivedImageMaximumBytes + 1));

        var closed = new MemoryStream([1]);
        closed.Dispose();
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateDerivedWriteRequest(Derived(), closed, 1));
    }

    [Fact]
    public void SourceCopy_RequiresSameMediaStagingToSourceAndStrongOpaqueEtag()
    {
        var request = MediaObjectAccessPolicy.CreateSourceCopyRequest(
            Staging(),
            Source(),
            "\"opaque-etag-123\"",
            "image/webp");

        Assert.Equal(Staging(), request.SourceObjectKey);
        Assert.Equal(Source(), request.DestinationObjectKey);
        Assert.Equal("\"opaque-etag-123\"", request.SourceETag);
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                Staging(),
                MediaObjectKey.Source(OtherMediaId, 1),
                "\"etag\"",
                "image/png"));
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                Source(),
                Source(),
                "\"etag\"",
                "image/png"));
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                Staging(),
                Derived(),
                "\"etag\"",
                "image/png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("etag")]
    [InlineData("W/\"etag\"")]
    [InlineData("\"contains space\"")]
    [InlineData("\"line\r\nbreak\"")]
    [InlineData("\"embedded\\backslash\"")]
    [InlineData("\"embedded\"quote\"")]
    public void SourceCopy_RejectsMalformedOrHeaderUnsafeEtag(string? etag)
    {
        Assert.False(MediaObjectAccessPolicy.IsValidETag(etag));
        Assert.Throws<ArgumentException>(() =>
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                Staging(),
                Source(),
                etag!,
                "image/png"));
    }

    private static MediaObjectKey Staging() =>
        MediaObjectKey.Staging(MediaId, UploadAttemptId);

    private static MediaObjectKey Source() => MediaObjectKey.Source(MediaId, 1);

    private static MediaObjectKey Derived() =>
        MediaObjectKey.Derived(MediaId, 1, MediaDerivedVariant.GameDisplay);
}
