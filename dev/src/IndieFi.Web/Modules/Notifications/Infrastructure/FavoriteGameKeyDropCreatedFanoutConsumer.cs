using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Contracts;
using MassTransit;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class FavoriteGameKeyDropCreatedFanoutConsumer :
    IConsumer<FavoriteGameKeyDropCreatedFanoutV1>
{
    private readonly FavoriteGameKeyDropFanoutService _fanout;

    public FavoriteGameKeyDropCreatedFanoutConsumer(
        FavoriteGameKeyDropFanoutService fanout)
    {
        _fanout = fanout;
    }

    public async Task Consume(
        ConsumeContext<FavoriteGameKeyDropCreatedFanoutV1> context)
    {
        var result = await _fanout.StageBatchAsync(
            context.Message,
            context.CancellationToken);
        if (result.NextAfterUserId is not long nextAfterUserId)
        {
            return;
        }

        var continuation = context.Message with
        {
            AfterUserId = nextAfterUserId,
        };
        await context.Publish(
            continuation,
            publish => publish.MessageId = NotificationFanoutMessageId.CreateContinuation(
                context.Message.FanoutId,
                nextAfterUserId),
            context.CancellationToken);
    }
}
