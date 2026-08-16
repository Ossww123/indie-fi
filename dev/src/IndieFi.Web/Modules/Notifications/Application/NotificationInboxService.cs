using IndieFi.Web.Data;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationCursor(
    DateTimeOffset CreatedAt,
    long NotificationId);

public sealed record NotificationListItem(
    long Id,
    NotificationKind Kind,
    string Title,
    string Body,
    string? DestinationPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record NotificationInboxSnapshot(
    IReadOnlyList<NotificationListItem> Items,
    int UnreadCount,
    NotificationCursor? NextCursor);

public enum NotificationReadStateOutcome
{
    Changed,
    Unchanged,
    NotFound,
}

public sealed class NotificationInboxService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public NotificationInboxService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<NotificationInboxSnapshot> GetAsync(
        long userId,
        int pageSize = DefaultPageSize,
        NotificationCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var now = _timeProvider.GetUtcNow();
        var visible = _db.Notifications
            .AsNoTracking()
            .Where(item => item.UserId == userId
                && item.IsWebVisible
                && item.ContentRemovedAt == null
                && item.ExpiresAt > now);
        if (cursor is not null)
        {
            visible = visible.Where(item =>
                item.CreatedAt < cursor.CreatedAt
                || (item.CreatedAt == cursor.CreatedAt
                    && item.Id < cursor.NotificationId));
        }

        var items = await visible
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(pageSize + 1)
            .Select(item => new NotificationListItem(
                item.Id,
                item.Kind,
                item.Title!,
                item.Body!,
                item.DestinationPath,
                item.CreatedAt,
                item.ReadAt))
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var unreadCount = await _db.Notifications
            .AsNoTracking()
            .CountAsync(item => item.UserId == userId
                && item.IsWebVisible
                && item.ReadAt == null
                && item.ContentRemovedAt == null
                && item.ExpiresAt > now,
                cancellationToken);
        var last = hasMore ? items[^1] : null;
        return new(
            items,
            unreadCount,
            last is null ? null : new(last.CreatedAt, last.Id));
    }

    public async Task<int> GetUnreadCountAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        var now = _timeProvider.GetUtcNow();
        return await _db.Notifications
            .AsNoTracking()
            .CountAsync(item => item.UserId == userId
                && item.IsWebVisible
                && item.ReadAt == null
                && item.ContentRemovedAt == null
                && item.ExpiresAt > now,
                cancellationToken);
    }

    public async Task<NotificationReadStateOutcome> SetReadStateAsync(
        long userId,
        long notificationId,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(notificationId);
        var now = _timeProvider.GetUtcNow();
        var changed = isRead
            ? await _db.Notifications
                .Where(item => item.Id == notificationId
                    && item.UserId == userId
                    && item.IsWebVisible
                    && item.ReadAt == null
                    && item.ContentRemovedAt == null
                    && item.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.ReadAt, now),
                    cancellationToken)
            : await _db.Notifications
                .Where(item => item.Id == notificationId
                    && item.UserId == userId
                    && item.IsWebVisible
                    && item.ReadAt != null
                    && item.ContentRemovedAt == null
                    && item.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        item => item.ReadAt,
                        (DateTimeOffset?)null),
                    cancellationToken);
        if (changed == 1)
        {
            return NotificationReadStateOutcome.Changed;
        }

        var exists = await _db.Notifications.AsNoTracking().AnyAsync(
            item => item.Id == notificationId
                && item.UserId == userId
                && item.IsWebVisible
                && item.ContentRemovedAt == null
                && item.ExpiresAt > now,
            cancellationToken);
        return exists
            ? NotificationReadStateOutcome.Unchanged
            : NotificationReadStateOutcome.NotFound;
    }

    public async Task<int> MarkAllReadAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        var now = _timeProvider.GetUtcNow();
        return await _db.Notifications
            .Where(item => item.UserId == userId
                && item.IsWebVisible
                && item.ReadAt == null
                && item.ContentRemovedAt == null
                && item.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.ReadAt, now),
                cancellationToken);
    }
}
