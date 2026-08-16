using System.Buffers;
using IndieFi.Web.Modules.Media.Domain;

namespace IndieFi.Web.Modules.Media.Application;

public interface IMediaImageProcessor
{
    Task<MediaImageProcessingResult> ProcessAsync(
        Stream source,
        long sourceLength,
        MediaAttachmentKind attachmentKind,
        CancellationToken cancellationToken = default);
}

public enum MediaPixelFormat
{
    Rgba8888UnpremultipliedSrgb,
}

/// <summary>
/// Owns a decoded sRGB pixel buffer. Encoded EXIF, GPS, XMP, IPTC and thumbnail
/// data are intentionally absent, so a derivative encoder cannot copy them.
/// </summary>
public sealed class MediaNormalizedImage : IDisposable
{
    private byte[]? _pixels;
    private readonly int _pixelLength;

    internal MediaNormalizedImage(
        byte[] pixels,
        int pixelLength,
        string detectedContentType,
        int pixelWidth,
        int pixelHeight)
    {
        _pixels = pixels;
        _pixelLength = pixelLength;
        DetectedContentType = detectedContentType;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public string DetectedContentType { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int FrameCount => 1;
    public MediaPixelFormat PixelFormat =>
        MediaPixelFormat.Rgba8888UnpremultipliedSrgb;

    public ReadOnlyMemory<byte> RgbaPixels
    {
        get
        {
            var pixels = _pixels
                ?? throw new ObjectDisposedException(nameof(MediaNormalizedImage));
            return pixels.AsMemory(0, _pixelLength);
        }
    }

    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
        {
            ArrayPool<byte>.Shared.Return(pixels, clearArray: true);
        }
    }
}

public sealed class MediaImageProcessingResult
{
    private MediaImageProcessingResult(
        MediaNormalizedImage? image,
        string? failureCode)
    {
        Image = image;
        FailureCode = failureCode;
    }

    public bool Succeeded => Image is not null;
    public MediaNormalizedImage? Image { get; }
    public string? FailureCode { get; }

    public static MediaImageProcessingResult Success(MediaNormalizedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new MediaImageProcessingResult(image, failureCode: null);
    }

    public static MediaImageProcessingResult PermanentFailure(string failureCode)
    {
        MediaProcessingFailureCode.RequireValid(failureCode);
        return new MediaImageProcessingResult(image: null, failureCode);
    }
}

public static class MediaImageFailureCodes
{
    public const string SourceBytesExceeded = "image_bytes_exceeded";
    public const string SourceLengthMismatch = "image_source_length_mismatch";
    public const string InvalidFormat = "image_invalid_format";
    public const string AnimatedImage = "image_animated";
    public const string DimensionsExceeded = "image_dimensions_exceeded";
    public const string DecodeFailed = "image_decode_failed";
}
