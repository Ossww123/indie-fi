using System.Buffers.Binary;
using System.Security.Cryptography;
using IndieFi.Web.Modules.Notifications.Domain;

namespace IndieFi.Web.Modules.Notifications.Application;

public static class NotificationEventId
{
    public static Guid Create(
        NotificationKind kind,
        long sourceId,
        long recipientUserId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipientUserId);

        Span<byte> input = stackalloc byte[20];
        BinaryPrimitives.WriteInt32BigEndian(input, (int)kind);
        BinaryPrimitives.WriteInt64BigEndian(input[4..], sourceId);
        BinaryPrimitives.WriteInt64BigEndian(input[12..], recipientUserId);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
