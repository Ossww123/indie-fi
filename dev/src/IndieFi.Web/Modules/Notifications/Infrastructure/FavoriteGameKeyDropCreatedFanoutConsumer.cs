using System.Buffers.Binary;
using System.Security.Cryptography;
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
            publish => publish.MessageId = CreateContinuationMessageId(
                context.Message.FanoutId,
                nextAfterUserId),
            context.CancellationToken);
    }

    private static Guid CreateContinuationMessageId(
        Guid fanoutId,
        long afterUserId)
    {
        Span<byte> input = stackalloc byte[28];
        fanoutId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt64BigEndian(input[16..], afterUserId);
        BinaryPrimitives.WriteInt32BigEndian(input[24..], 1);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
