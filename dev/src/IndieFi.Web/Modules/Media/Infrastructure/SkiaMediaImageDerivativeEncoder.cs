using System.Runtime.InteropServices;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using SkiaSharp;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class SkiaMediaImageDerivativeEncoder : IMediaImageDerivativeEncoder
{
    private const int WebpQuality = 85;
    private static readonly SKSamplingOptions Sampling =
        new(SKCubicResampler.Mitchell);

    public IReadOnlyList<MediaImageDerivative> Encode(
        MediaNormalizedImage image,
        MediaAttachmentKind attachmentKind)
    {
        ArgumentNullException.ThrowIfNull(image);
        var variants = GetVariants(attachmentKind);
        if (!MemoryMarshal.TryGetArray(
                image.RgbaPixels,
                out ArraySegment<byte> pixels)
            || pixels.Array is null)
        {
            throw new InvalidOperationException(
                "The normalized image does not expose an array-backed pixel buffer.");
        }

        using var srgb = SKColorSpace.CreateSrgb();
        var sourceInfo = new SKImageInfo(
            image.PixelWidth,
            image.PixelHeight,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            srgb);
        var pinnedPixels = GCHandle.Alloc(pixels.Array, GCHandleType.Pinned);
        try
        {
            using var source = new SKBitmap();
            var address = IntPtr.Add(
                pinnedPixels.AddrOfPinnedObject(),
                pixels.Offset);
            if (!source.InstallPixels(sourceInfo, address, sourceInfo.RowBytes))
            {
                throw new InvalidOperationException(
                    "The normalized pixel buffer could not be installed for encoding.");
            }

            return variants
                .Select(variant => EncodeVariant(source, variant))
                .ToArray();
        }
        finally
        {
            pinnedPixels.Free();
        }
    }

    private static MediaImageDerivative EncodeVariant(
        SKBitmap source,
        VariantDefinition definition)
    {
        var (width, height) = FitWithinLongEdge(
            source.Width,
            source.Height,
            definition.MaximumLongEdge);
        SKBitmap? resized = null;
        try
        {
            var bitmap = source;
            if (width != source.Width || height != source.Height)
            {
                var targetInfo = source.Info.WithSize(width, height);
                resized = source.Resize(targetInfo, Sampling)
                    ?? throw new InvalidOperationException(
                        "The normalized image could not be resized.");
                bitmap = resized;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(
                SKEncodedImageFormat.Webp,
                WebpQuality)
                ?? throw new InvalidOperationException(
                    "The media derivative could not be encoded as WebP.");
            return new MediaImageDerivative(
                definition.Variant,
                width,
                height,
                encoded.ToArray());
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private static (int Width, int Height) FitWithinLongEdge(
        int width,
        int height,
        int maximumLongEdge)
    {
        var longEdge = Math.Max(width, height);
        if (longEdge <= maximumLongEdge)
        {
            return (width, height);
        }

        var scale = maximumLongEdge / (double)longEdge;
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static VariantDefinition[] GetVariants(
        MediaAttachmentKind attachmentKind) =>
        attachmentKind switch
        {
            MediaAttachmentKind.GameCover or
                MediaAttachmentKind.GameScreenshot =>
            [
                new(MediaDerivedVariant.GameDisplay, MaximumLongEdge: 1_600),
                new(MediaDerivedVariant.GameList, MaximumLongEdge: 640),
            ],
            MediaAttachmentKind.ProfileImage =>
            [
                new(MediaDerivedVariant.ProfileDisplay, MaximumLongEdge: 512),
                new(MediaDerivedVariant.ProfileThumbnail, MaximumLongEdge: 128),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(attachmentKind)),
        };

    private sealed record VariantDefinition(
        MediaDerivedVariant Variant,
        int MaximumLongEdge);
}
