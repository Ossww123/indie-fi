using System.Data;
using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationMaintenanceResult(
    int ContentRemovedCount,
    int DeliveryDeletedCount,
    int OutboxStateDeletedCount,
    int InboxStateDeletedCount)
{
    public int TotalCount =>
        ContentRemovedCount
        + DeliveryDeletedCount
        + OutboxStateDeletedCount
        + InboxStateDeletedCount;
}

public sealed class NotificationMaintenanceService
{
    private const string CleanupSql =
        """
        SELECT content_removed_count,
               delivery_deleted_count,
               outbox_state_deleted_count,
               inbox_state_deleted_count
          FROM public.cleanup_notification_records(@now, @batch_size)
        """;

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public NotificationMaintenanceService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<NotificationMaintenanceResult> RunBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = new NpgsqlCommand(CleanupSql, connection);
            command.Parameters.AddWithValue("now", _timeProvider.GetUtcNow());
            command.Parameters.AddWithValue("batch_size", batchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Notification cleanup did not return a result.");
            }

            return new(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3));
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
