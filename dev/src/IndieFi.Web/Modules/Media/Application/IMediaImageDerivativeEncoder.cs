using IndieFi.Web.Modules.Media.Domain;

namespace IndieFi.Web.Modules.Media.Application;

public interface IMediaImageDerivativeEncoder
{
    IReadOnlyList<MediaImageDerivative> Encode(
        MediaNormalizedImage image,
        MediaAttachmentKind attachmentKind);
}

public sealed class MediaImageDerivative
{
    private readonly byte[] _content;

    internal MediaImageDerivative(
        MediaDerivedVariant variant,
        int pixelWidth,
        int pixelHeight,
        byte[] content)
    {
        Variant = variant;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        _content = content;
    }

    public MediaDerivedVariant Variant { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int ContentLength => _content.Length;
    public ReadOnlyMemory<byte> Content => _content;

    public Stream OpenRead() => new MemoryStream(_content, writable: false);
}
