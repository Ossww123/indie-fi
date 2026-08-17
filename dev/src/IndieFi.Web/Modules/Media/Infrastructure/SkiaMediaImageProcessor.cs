using System.Buffers;
using System.Buffers.Binary;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using SkiaSharp;

namespace IndieFi.Web.Modules.Media.Infrastructure;

public sealed class SkiaMediaImageProcessor : IMediaImageProcessor
{
    private const int BytesPerPixel = 4;

    public async Task<MediaImageProcessingResult> ProcessAsync(
        Stream source,
        long sourceLength,
        MediaAttachmentKind attachmentKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The media source must be readable.", nameof(source));
        }

        var limits = MediaPolicy.GetImageLimits(attachmentKind);
        if (sourceLength > limits.MaximumOriginalBytes)
        {
            return Failure(MediaImageFailureCodes.SourceBytesExceeded);
        }

        if (sourceLength <= 0)
        {
            return Failure(MediaImageFailureCodes.SourceLengthMismatch);
        }

        var encoded = new byte[checked((int)sourceLength)];
        if (!await ReadExactSourceAsync(source, encoded, cancellationToken))
        {
            return Failure(MediaImageFailureCodes.SourceLengthMismatch);
        }

        using var encodedStream = new MemoryStream(encoded, writable: false);
        using var codec = SKCodec.Create(encodedStream);
        if (codec is null)
        {
            return Failure(MediaImageFailureCodes.InvalidFormat);
        }

        var detectedContentType = GetAllowedContentType(codec.EncodedFormat);
        if (detectedContentType is null)
        {
            return Failure(MediaImageFailureCodes.InvalidFormat);
        }

        if (HasAnimationChunks(codec.EncodedFormat, encoded))
        {
            return Failure(MediaImageFailureCodes.AnimatedImage);
        }

        var frameCount = Math.Max(1, codec.FrameCount);
        if (frameCount != 1)
        {
            return Failure(MediaImageFailureCodes.AnimatedImage);
        }

        var sourceInfo = codec.Info;
        if (!IsWithinLimits(sourceInfo.Width, sourceInfo.Height, limits))
        {
            return Failure(MediaImageFailureCodes.DimensionsExceeded);
        }

        var pixelLength = checked(sourceInfo.Width * sourceInfo.Height * BytesPerPixel);
        byte[]? decodedPixels = ArrayPool<byte>.Shared.Rent(pixelLength);
        using var srgb = SKColorSpace.CreateSrgb();
        var decodeInfo = new SKImageInfo(
            sourceInfo.Width,
            sourceInfo.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul,
            srgb);

        try
        {
            var decodeResult = codec.GetPixels(decodeInfo, decodedPixels);
            if (decodeResult != SKCodecResult.Success)
            {
                return Failure(MediaImageFailureCodes.DecodeFailed);
            }

            var normalized = ApplyEncodedOrientation(
                decodedPixels,
                pixelLength,
                sourceInfo.Width,
                sourceInfo.Height,
                codec.EncodedOrigin);
            decodedPixels = null;
            return MediaImageProcessingResult.Success(new MediaNormalizedImage(
                normalized.Pixels,
                pixelLength,
                detectedContentType,
                normalized.Width,
                normalized.Height));
        }
        finally
        {
            if (decodedPixels is not null)
            {
                ArrayPool<byte>.Shared.Return(decodedPixels, clearArray: true);
            }
        }
    }

    private static async Task<bool> ReadExactSourceAsync(
        Stream source,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await source.ReadAsync(
                destination.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        var extra = new byte[1];
        return await source.ReadAsync(extra, cancellationToken) == 0;
    }

    private static string? GetAllowedContentType(SKEncodedImageFormat format) =>
        format switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => null,
        };

    private static bool HasAnimationChunks(
        SKEncodedImageFormat format,
        ReadOnlySpan<byte> encoded) =>
        format switch
        {
            SKEncodedImageFormat.Png => HasPngAnimationControl(encoded),
            SKEncodedImageFormat.Webp => HasWebpAnimationControl(encoded),
            _ => false,
        };

    private static bool HasPngAnimationControl(ReadOnlySpan<byte> encoded)
    {
        const int signatureLength = 8;
        const int chunkOverhead = 12;
        var offset = signatureLength;
        while (offset <= encoded.Length - chunkOverhead)
        {
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(
                encoded.Slice(offset, sizeof(uint)));
            if (dataLength > int.MaxValue
                || dataLength > encoded.Length - offset - chunkOverhead)
            {
                return false;
            }

            var chunkType = encoded.Slice(offset + sizeof(uint), 4);
            if (chunkType.SequenceEqual("acTL"u8))
            {
                return true;
            }

            offset += chunkOverhead + (int)dataLength;
        }

        return false;
    }

    private static bool HasWebpAnimationControl(ReadOnlySpan<byte> encoded)
    {
        const int riffHeaderLength = 12;
        const int chunkHeaderLength = 8;
        var offset = riffHeaderLength;
        while (offset <= encoded.Length - chunkHeaderLength)
        {
            var chunkType = encoded.Slice(offset, 4);
            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(
                encoded.Slice(offset + 4, sizeof(uint)));
            if (dataLength > int.MaxValue
                || dataLength > encoded.Length - offset - chunkHeaderLength)
            {
                return false;
            }

            if (chunkType.SequenceEqual("ANIM"u8)
                || chunkType.SequenceEqual("ANMF"u8)
                || (chunkType.SequenceEqual("VP8X"u8)
                    && dataLength > 0
                    && (encoded[offset + chunkHeaderLength] & 0x02) != 0))
            {
                return true;
            }

            var paddedDataLength = (long)dataLength + (dataLength & 1);
            var nextOffset = offset + chunkHeaderLength + paddedDataLength;
            if (nextOffset > encoded.Length)
            {
                return false;
            }

            offset = (int)nextOffset;
        }

        return false;
    }

    private static bool IsWithinLimits(
        int width,
        int height,
        MediaImageLimits limits) =>
        width > 0
        && height > 0
        && width <= limits.MaximumWidth
        && height <= limits.MaximumHeight
        && (long)width * height <= limits.MaximumDecodedPixels;

    private static NormalizedPixels ApplyEncodedOrientation(
        byte[] source,
        int pixelLength,
        int sourceWidth,
        int sourceHeight,
        SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return new NormalizedPixels(source, sourceWidth, sourceHeight);
        }

        var swapsDimensions = origin is
            SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        var targetWidth = swapsDimensions ? sourceHeight : sourceWidth;
        var targetHeight = swapsDimensions ? sourceWidth : sourceHeight;
        var target = ArrayPool<byte>.Shared.Rent(pixelLength);
        try
        {
            var sourceSpan = source.AsSpan(0, pixelLength);
            var targetSpan = target.AsSpan(0, pixelLength);

            for (var y = 0; y < sourceHeight; y++)
            {
                for (var x = 0; x < sourceWidth; x++)
                {
                    var (targetX, targetY) = MapPixel(
                        x,
                        y,
                        sourceWidth,
                        sourceHeight,
                        origin);
                    var sourceOffset = ((y * sourceWidth) + x) * BytesPerPixel;
                    var targetOffset = ((targetY * targetWidth) + targetX) * BytesPerPixel;
                    sourceSpan.Slice(sourceOffset, BytesPerPixel)
                        .CopyTo(targetSpan.Slice(targetOffset, BytesPerPixel));
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(target, clearArray: true);
            throw;
        }

        ArrayPool<byte>.Shared.Return(source, clearArray: true);
        return new NormalizedPixels(target, targetWidth, targetHeight);
    }

    private static (int X, int Y) MapPixel(
        int x,
        int y,
        int width,
        int height,
        SKEncodedOrigin origin) =>
        origin switch
        {
            SKEncodedOrigin.TopRight => (width - 1 - x, y),
            SKEncodedOrigin.BottomRight => (width - 1 - x, height - 1 - y),
            SKEncodedOrigin.BottomLeft => (x, height - 1 - y),
            SKEncodedOrigin.LeftTop => (y, x),
            SKEncodedOrigin.RightTop => (height - 1 - y, x),
            SKEncodedOrigin.RightBottom => (height - 1 - y, width - 1 - x),
            SKEncodedOrigin.LeftBottom => (y, width - 1 - x),
            _ => (x, y),
        };

    private static MediaImageProcessingResult Failure(string failureCode) =>
        MediaImageProcessingResult.PermanentFailure(failureCode);

    private sealed record NormalizedPixels(byte[] Pixels, int Width, int Height);
}
