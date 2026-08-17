using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IndieFi.Web.Modules.Media.Application;

public static class MediaDeletionEventId
{
    private const int DeletionEventDiscriminator = 1;

    public static Guid Create(Guid mediaId, int revision)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(mediaId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        Span<byte> input = stackalloc byte[24];
        mediaId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt32BigEndian(input[16..], revision);
        BinaryPrimitives.WriteInt32BigEndian(
            input[20..],
            DeletionEventDiscriminator);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
