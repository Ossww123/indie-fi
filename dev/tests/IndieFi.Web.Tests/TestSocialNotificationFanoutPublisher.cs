using IndieFi.Web.Modules.Notifications.Application;

namespace IndieFi.Web.Tests;

internal sealed record StagedSocialFanout(
    Guid FanoutId,
    long GameId,
    DateTimeOffset OccurredAt);

internal sealed class TestSocialNotificationFanoutPublisher :
    ISocialNotificationFanoutPublisher
{
    public List<StagedSocialFanout> Events { get; } = [];
    public Exception? Failure { get; init; }

    public Task StageGamePublishedAsync(
        Guid fanoutId,
        long gameId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        Events.Add(new StagedSocialFanout(fanoutId, gameId, occurredAt));
        return Task.CompletedTask;
    }
}
