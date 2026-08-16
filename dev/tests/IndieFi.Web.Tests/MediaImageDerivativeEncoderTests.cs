using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using SkiaSharp;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MediaImageDerivativeEncoderTests
{
    private readonly IMediaImageProcessor _processor = new SkiaMediaImageProcessor();
    private readonly IMediaImageDerivativeEncoder _encoder =
        new SkiaMediaImageDerivativeEncoder();

    [Fact]
    public async Task GameImage_CreatesDisplayAndListWebpWithoutUpscaling()
    {
        using var large = await NormalizeAsync(3_200, 1_800);
        using var small = await NormalizeAsync(400, 225);

        var largeDerivatives = _encoder.Encode(
            large,
            MediaAttachmentKind.GameScreenshot);
        var smallDerivatives = _encoder.Encode(
            small,
            MediaAttachmentKind.GameCover);

        AssertDerivative(
            largeDerivatives,
            MediaDerivedVariant.GameDisplay,
            width: 1_600,
            height: 900);
        AssertDerivative(
            largeDerivatives,
            MediaDerivedVariant.GameList,
            width: 640,
            height: 360);
        AssertDerivative(
            smallDerivatives,
            MediaDerivedVariant.GameDisplay,
            width: 400,
            height: 225);
        AssertDerivative(
            smallDerivatives,
            MediaDerivedVariant.GameList,
            width: 400,
            height: 225);
    }

    [Fact]
    public async Task ProfileImage_CreatesDisplayAndThumbnailWebp()
    {
        using var image = await NormalizeAsync(1_000, 2_000);

        var derivatives = _encoder.Encode(
            image,
            MediaAttachmentKind.ProfileImage);

        AssertDerivative(
            derivatives,
            MediaDerivedVariant.ProfileDisplay,
            width: 256,
            height: 512);
        AssertDerivative(
            derivatives,
            MediaDerivedVariant.ProfileThumbnail,
            width: 64,
            height: 128);
    }

    [Fact]
    public async Task SameNormalizedPixels_ProduceIdenticalDerivativeBytes()
    {
        using var image = await NormalizeAsync(800, 450);

        var first = _encoder.Encode(image, MediaAttachmentKind.GameCover);
        var second = _encoder.Encode(image, MediaAttachmentKind.GameCover);

        Assert.Equal(first.Select(item => item.Variant), second.Select(item => item.Variant));
        Assert.Equal(
            first.Select(item => item.Content.ToArray()),
            second.Select(item => item.Content.ToArray()),
            ByteArrayComparer.Instance);
    }

    private async Task<MediaNormalizedImage> NormalizeAsync(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, quality: 100);
        var bytes = encoded.ToArray();
        var result = await _processor.ProcessAsync(
            new MemoryStream(bytes, writable: false),
            bytes.Length,
            MediaAttachmentKind.GameScreenshot,
            TestContext.Current.CancellationToken);
        return Assert.IsType<MediaNormalizedImage>(result.Image);
    }

    private static void AssertDerivative(
        IReadOnlyList<MediaImageDerivative> derivatives,
        MediaDerivedVariant variant,
        int width,
        int height)
    {
        var derivative = Assert.Single(derivatives, item => item.Variant == variant);
        Assert.Equal(width, derivative.PixelWidth);
        Assert.Equal(height, derivative.PixelHeight);
        Assert.Equal(derivative.Content.Length, derivative.ContentLength);
        Assert.True(derivative.ContentLength > 0);

        using var stream = new MemoryStream(derivative.Content.ToArray(), writable: false);
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(width, codec.Info.Width);
        Assert.Equal(height, codec.Info.Height);
        Assert.InRange(codec.FrameCount, 0, 1);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] value) => value.Length;
    }
}
