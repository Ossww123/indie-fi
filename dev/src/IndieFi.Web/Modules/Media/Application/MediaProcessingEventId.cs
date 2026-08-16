using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IndieFi.Web.Modules.Media.Application;

public static class MediaProcessingEventId
{
    public static Guid Create(Guid mediaId, int revision)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(mediaId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        Span<byte> input = stackalloc byte[20];
        mediaId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt32BigEndian(input[16..], revision);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
