using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using System.IO.Compression;
using SkiaSharp;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MediaImageProcessorTests
{
    private readonly IMediaImageProcessor _processor = new SkiaMediaImageProcessor();

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public async Task StillImage_DetectsTheDecodedFormatAndReturnsSrgbPixels(
        SKEncodedImageFormat format,
        string expectedContentType)
    {
        var encoded = EncodeStillImage(format, width: 4, height: 3);

        var result = await ProcessAsync(encoded);

        Assert.True(result.Succeeded);
        Assert.Null(result.FailureCode);
        using var image = Assert.IsType<MediaNormalizedImage>(result.Image);
        Assert.Equal(expectedContentType, image.DetectedContentType);
        Assert.Equal(4, image.PixelWidth);
        Assert.Equal(3, image.PixelHeight);
        Assert.Equal(1, image.FrameCount);
        Assert.Equal(4 * 3 * 4, image.RgbaPixels.Length);
        Assert.Equal(MediaPixelFormat.Rgba8888UnpremultipliedSrgb, image.PixelFormat);
    }

    [Fact]
    public async Task UnsupportedOrMalformedContainer_IsPermanentlyRejected()
    {
        var gif = Convert.FromBase64String(
            "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

        var unsupported = await ProcessAsync(gif);
        var malformed = await ProcessAsync([0x49, 0x6E, 0x64, 0x69, 0x65]);

        Assert.False(unsupported.Succeeded);
        Assert.Equal(MediaImageFailureCodes.InvalidFormat, unsupported.FailureCode);
        Assert.False(malformed.Succeeded);
        Assert.Equal(MediaImageFailureCodes.InvalidFormat, malformed.FailureCode);
    }

    [Fact]
    public async Task AnimatedWebp_IsPermanentlyRejected()
    {
        using var first = CreateBitmap(4, 3, SKColors.Red);
        using var second = CreateBitmap(4, 3, SKColors.Blue);
        var frames = new[]
        {
            new SKWebpEncoderFrame(first, TimeSpan.FromMilliseconds(100)),
            new SKWebpEncoderFrame(second, TimeSpan.FromMilliseconds(100)),
        };
        using var data = SKWebpEncoder.EncodeAnimated(
            frames,
            new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Animated WebP encoding failed.");
        var encoded = data.ToArray();

        var result = await ProcessAsync(encoded);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaImageFailureCodes.AnimatedImage, result.FailureCode);
    }

    [Fact]
    public async Task AnimatedPng_IsPermanentlyRejected()
    {
        var encoded = CreateAnimatedPng();

        var result = await ProcessAsync(encoded);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaImageFailureCodes.AnimatedImage, result.FailureCode);
    }

    [Theory]
    [InlineData(12_001, 1)]
    [InlineData(8_000, 5_001)]
    public async Task OversizedDecodedDimensions_AreRejectedBeforePixelAllocation(
        int width,
        int height)
    {
        var encoded = CreateHighlyCompressedPng(width, height);

        var result = await ProcessAsync(encoded);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaImageFailureCodes.DimensionsExceeded, result.FailureCode);
    }

    [Fact]
    public async Task ProfileImage_UsesTheSmallerDecodedPixelLimit()
    {
        var encoded = CreateHighlyCompressedPng(width: 8_000, height: 2_001);

        var result = await ProcessAsync(
            encoded,
            MediaAttachmentKind.ProfileImage);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaImageFailureCodes.DimensionsExceeded, result.FailureCode);
    }

    [Fact]
    public async Task DeclaredLengthMismatch_IsRejectedAfterReadingTheWholeSource()
    {
        var encoded = EncodeStillImage(SKEncodedImageFormat.Png, 4, 3);

        var shorterThanDeclared = await _processor.ProcessAsync(
            new NonSeekableReadStream(encoded),
            encoded.Length + 1,
            MediaAttachmentKind.GameScreenshot,
            TestContext.Current.CancellationToken);
        var longerThanDeclared = await _processor.ProcessAsync(
            new NonSeekableReadStream(encoded),
            encoded.Length - 1,
            MediaAttachmentKind.GameScreenshot,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            MediaImageFailureCodes.SourceLengthMismatch,
            shorterThanDeclared.FailureCode);
        Assert.Equal(
            MediaImageFailureCodes.SourceLengthMismatch,
            longerThanDeclared.FailureCode);
    }

    [Fact]
    public async Task OversizedSource_IsRejectedWithoutReadingIt()
    {
        var source = new ThrowOnReadStream();

        var result = await _processor.ProcessAsync(
            source,
            MediaPolicy.GameImageMaximumBytes + 1,
            MediaAttachmentKind.GameCover,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaImageFailureCodes.SourceBytesExceeded, result.FailureCode);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public async Task ExifOrientation_IsAppliedToTheMetadataFreePixelBuffer()
    {
        var encoded = AddExifOrientation(
            EncodeQuadrantsAsJpeg(width: 20, height: 30),
            orientation: 6);

        var result = await ProcessAsync(encoded);

        using var image = Assert.IsType<MediaNormalizedImage>(result.Image);
        Assert.Equal(30, image.PixelWidth);
        Assert.Equal(20, image.PixelHeight);
        AssertDominantColor(image, x: 2, y: 2, SKColors.Blue);
        AssertDominantColor(image, x: 27, y: 2, SKColors.Red);
        AssertDominantColor(image, x: 2, y: 17, SKColors.Yellow);
        AssertDominantColor(image, x: 27, y: 17, SKColors.Green);
    }

    [Fact]
    public async Task TruncatedImage_IsRejectedAfterDecode()
    {
        var encoded = EncodeStillImage(SKEncodedImageFormat.Jpeg, 20, 20);
        var truncated = encoded[..^2];

        var result = await ProcessAsync(truncated);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaImageFailureCodes.DecodeFailed, result.FailureCode);
    }

    private Task<MediaImageProcessingResult> ProcessAsync(
        byte[] encoded,
        MediaAttachmentKind attachmentKind = MediaAttachmentKind.GameScreenshot) =>
        _processor.ProcessAsync(
            new NonSeekableReadStream(encoded),
            encoded.Length,
            attachmentKind,
            TestContext.Current.CancellationToken);

    private static byte[] EncodeStillImage(
        SKEncodedImageFormat format,
        int width,
        int height)
    {
        using var bitmap = CreateBitmap(width, height, SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality: 100);
        return data.ToArray();
    }

    private static byte[] EncodeQuadrantsAsJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        var halfWidth = width / 2f;
        var halfHeight = height / 2f;
        canvas.DrawRect(0, 0, halfWidth, halfHeight, new SKPaint { Color = SKColors.Red });
        canvas.DrawRect(halfWidth, 0, halfWidth, halfHeight, new SKPaint { Color = SKColors.Green });
        canvas.DrawRect(0, halfHeight, halfWidth, halfHeight, new SKPaint { Color = SKColors.Blue });
        canvas.DrawRect(halfWidth, halfHeight, halfWidth, halfHeight, new SKPaint { Color = SKColors.Yellow });
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality: 100);
        return data.ToArray();
    }

    private static SKBitmap CreateBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        return bitmap;
    }

    private static byte[] AddExifOrientation(byte[] jpeg, ushort orientation)
    {
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);

        byte[] exif =
        [
            0x45, 0x78, 0x69, 0x66, 0x00, 0x00,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
            (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        var segmentLength = exif.Length + 2;
        var result = new byte[jpeg.Length + exif.Length + 4];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1;
        result[4] = (byte)(segmentLength >> 8);
        result[5] = (byte)segmentLength;
        exif.CopyTo(result, 6);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(6 + exif.Length));
        return result;
    }

    private static byte[] CreateHighlyCompressedPng(int width, int height)
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;

        using var stream = new MemoryStream();
        stream.Write(signature);
        WritePngChunk(stream, "IHDR"u8, ihdr);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
            compressed,
            CompressionLevel.Fastest,
            leaveOpen: true))
        {
            var transparentRow = new byte[checked((width * 4) + 1)];
            for (var row = 0; row < height; row++)
            {
                zlib.Write(transparentRow);
            }
        }

        WritePngChunk(stream, "IDAT"u8, compressed.ToArray());
        WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static byte[] CreateAnimatedPng()
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, 2);
        WriteBigEndian(ihdr, 4, 1);
        ihdr[8] = 8;
        ihdr[9] = 6;

        byte[] animationControl = new byte[8];
        WriteBigEndian(animationControl, 0, 2);
        var firstFrame = CreatePngFrameControl(sequence: 0);
        var secondFrame = CreatePngFrameControl(sequence: 1);
        var firstPixels = CompressPngPixels([0, 255, 0, 0, 255, 255, 0, 0, 255]);
        var secondPixels = CompressPngPixels([0, 0, 0, 255, 255, 0, 0, 255, 255]);
        var secondFrameData = new byte[secondPixels.Length + 4];
        WriteBigEndian(secondFrameData, 0, 2);
        secondPixels.CopyTo(secondFrameData, 4);

        using var stream = new MemoryStream();
        stream.Write(signature);
        WritePngChunk(stream, "IHDR"u8, ihdr);
        WritePngChunk(stream, "acTL"u8, animationControl);
        WritePngChunk(stream, "fcTL"u8, firstFrame);
        WritePngChunk(stream, "IDAT"u8, firstPixels);
        WritePngChunk(stream, "fcTL"u8, secondFrame);
        WritePngChunk(stream, "fdAT"u8, secondFrameData);
        WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static byte[] CreatePngFrameControl(int sequence)
    {
        byte[] frameControl = new byte[26];
        WriteBigEndian(frameControl, 0, sequence);
        WriteBigEndian(frameControl, 4, 2);
        WriteBigEndian(frameControl, 8, 1);
        frameControl[20] = 1;
        frameControl[22] = 10;
        return frameControl;
    }

    private static byte[] CompressPngPixels(byte[] pixels)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
            compressed,
            CompressionLevel.Fastest,
            leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        return compressed.ToArray();
    }

    private static void WritePngChunk(
        Stream stream,
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(crcInput)));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static void WriteBigEndian(Span<byte> target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void AssertDominantColor(
        MediaNormalizedImage image,
        int x,
        int y,
        SKColor expected)
    {
        var offset = ((y * image.PixelWidth) + x) * 4;
        var pixels = image.RgbaPixels.Span;
        Assert.InRange(Math.Abs(pixels[offset] - expected.Red), 0, 25);
        Assert.InRange(Math.Abs(pixels[offset + 1] - expected.Green), 0, 25);
        Assert.InRange(Math.Abs(pixels[offset + 2] - expected.Blue), 0, 25);
        Assert.Equal(255, pixels[offset + 3]);
    }

    private sealed class NonSeekableReadStream(byte[] contents) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, 7), contents.Length - _offset);
            contents.AsSpan(_offset, read).CopyTo(buffer.AsSpan(offset));
            _offset += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(Math.Min(buffer.Length, 7), contents.Length - _offset);
            contents.AsMemory(_offset, read).CopyTo(buffer);
            _offset += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("The source must not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("The source must not be read.");
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
