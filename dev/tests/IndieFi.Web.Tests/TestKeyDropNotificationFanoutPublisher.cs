using IndieFi.Web.Modules.Notifications.Application;

namespace IndieFi.Web.Tests;

internal sealed record StagedKeyDropFanout(
    Guid FanoutId,
    long DropId,
    DateTimeOffset OccurredAt);

internal sealed class TestKeyDropNotificationFanoutPublisher :
    IKeyDropNotificationFanoutPublisher
{
    public List<StagedKeyDropFanout> Events { get; } = [];
    public Exception? Failure { get; init; }

    public Task StageFavoriteGameCreatedAsync(
        Guid fanoutId,
        long dropId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Events.Add(new StagedKeyDropFanout(fanoutId, dropId, occurredAt));
        return Task.CompletedTask;
    }
}
