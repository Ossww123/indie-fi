using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class MassTransitNotificationEventPublisher : INotificationEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitNotificationEventPublisher(IPublishEndpoint publishEndpoint)
    {
        _publishEndpoint = publishEndpoint;
    }

    public Task StageAsync(
        Guid eventId,
        long recipientUserId,
        NotificationKind kind,
        string sourceType,
        long sourceId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(eventId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recipientUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        if (!IsValidSourceType(sourceType))
        {
            throw new ArgumentException(
                "Source type must use lowercase ASCII letters, digits, or underscores.",
                nameof(sourceType));
        }

        var message = new UserNotificationRequestedV1(
            eventId,
            recipientUserId,
            kind.ToString(),
            sourceType,
            sourceId,
            occurredAt.ToUniversalTime());

        return _publishEndpoint.Publish(
            message,
            context => context.MessageId = eventId,
            cancellationToken);
    }

    private static bool IsValidSourceType(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= NotificationPolicy.SourceTypeMaxLength
        && value.All(character =>
            char.IsAsciiLetterLower(character)
            || char.IsAsciiDigit(character)
            || character == '_');
}
