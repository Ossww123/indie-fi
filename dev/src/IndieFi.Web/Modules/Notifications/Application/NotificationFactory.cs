using System.Buffers.Binary;
using System.Security.Cryptography;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed class NotificationFactory
{
    public Notification Create(
        UserNotificationRequestedV1 message,
        NotificationKind kind,
        NotificationContent content,
        NotificationPreference? preference,
        DateTimeOffset createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(message.EventId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(message.RecipientUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(message.SourceId);
        ValidateContent(content);

        var channels = ResolveChannels(kind, preference);
        var notification = new Notification
        {
            UserId = message.RecipientUserId,
            EventId = message.EventId,
            Kind = kind,
            SourceType = message.SourceType,
            SourceId = message.SourceId,
            Title = content.Title,
            Body = content.Body,
            DestinationPath = content.DestinationPath,
            IsWebVisible = channels.WebEnabled,
            OccurredAt = message.OccurredAt.ToUniversalTime(),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.Add(NotificationPolicy.WebRetention),
        };

        notification.Deliveries.Add(CreateWebDelivery(
            message,
            channels.WebEnabled,
            createdAt));
        notification.Deliveries.Add(CreateEmailDelivery(
            message,
            channels.EmailEnabled,
            createdAt));
        return notification;
    }

    private static NotificationChannelDefaults ResolveChannels(
        NotificationKind kind,
        NotificationPreference? preference)
    {
        var defaults = NotificationPolicy.GetDefaults(kind);
        if (defaults.IsMandatory)
        {
            return defaults;
        }

        return preference is null
            ? defaults
            : new NotificationChannelDefaults(
                preference.WebEnabled,
                preference.EmailEnabled,
                IsMandatory: false);
    }

    private static NotificationDelivery CreateWebDelivery(
        UserNotificationRequestedV1 message,
        bool enabled,
        DateTimeOffset createdAt) => new()
    {
        MessageId = CreateDeliveryMessageId(
            message.EventId,
            message.RecipientUserId,
            NotificationChannel.Web),
        Channel = NotificationChannel.Web,
        Status = enabled
            ? NotificationDeliveryStatus.Delivered
            : NotificationDeliveryStatus.Suppressed,
        CreatedAt = createdAt,
        DeliveredAt = enabled ? createdAt : null,
    };

    private static NotificationDelivery CreateEmailDelivery(
        UserNotificationRequestedV1 message,
        bool enabled,
        DateTimeOffset createdAt) => new()
    {
        MessageId = CreateDeliveryMessageId(
            message.EventId,
            message.RecipientUserId,
            NotificationChannel.Email),
        Channel = NotificationChannel.Email,
        Status = enabled
            ? NotificationDeliveryStatus.Pending
            : NotificationDeliveryStatus.Suppressed,
        CreatedAt = createdAt,
        NextAttemptAt = enabled ? createdAt : null,
    };

    private static Guid CreateDeliveryMessageId(
        Guid eventId,
        long recipientUserId,
        NotificationChannel channel)
    {
        Span<byte> input = stackalloc byte[28];
        eventId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], recipientUserId);
        BinaryPrimitives.WriteInt32BigEndian(input[24..], (int)channel);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }

    private static void ValidateContent(NotificationContent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(content.Body);
        if (content.Title.Length > NotificationPolicy.TitleMaxLength)
        {
            throw new ArgumentException("Notification title is too long.", nameof(content));
        }

        if (content.Body.Length > NotificationPolicy.BodyMaxLength)
        {
            throw new ArgumentException("Notification body is too long.", nameof(content));
        }

        if (content.DestinationPath is not null
            && (content.DestinationPath.Length > NotificationPolicy.DestinationPathMaxLength
                || !content.DestinationPath.StartsWith("/", StringComparison.Ordinal)
                || content.DestinationPath.StartsWith("//", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Notification destination must be a local absolute path.",
                nameof(content));
        }
    }
}
