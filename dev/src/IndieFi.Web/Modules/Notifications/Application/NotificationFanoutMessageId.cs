using System.Buffers.Binary;
using System.Security.Cryptography;

namespace IndieFi.Web.Modules.Notifications.Application;

public static class NotificationFanoutMessageId
{
    public static Guid CreateContinuation(Guid fanoutId, long afterUserId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(fanoutId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(afterUserId);

        Span<byte> input = stackalloc byte[28];
        fanoutId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], afterUserId);
        BinaryPrimitives.WriteInt32BigEndian(input[24..], 1);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
