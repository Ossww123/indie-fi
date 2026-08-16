using System.Buffers.Binary;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Notifications.Contracts;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public enum NotificationProjectionOutcome
{
    Created,
    AlreadyExists,
    IgnoredInvalidContract,
    IgnoredMissingOrStaleSource,
}

public sealed record NotificationProjectionResult(
    NotificationProjectionOutcome Outcome,
    long? NotificationId = null);

public sealed class NotificationProjectionService
{
    private readonly AppDbContext _db;
    private readonly NotificationFactory _factory;
    private readonly TimeProvider _timeProvider;

    public NotificationProjectionService(
        AppDbContext db,
        NotificationFactory factory,
        TimeProvider timeProvider)
    {
        _db = db;
        _factory = factory;
        _timeProvider = timeProvider;
    }

    public async Task<NotificationProjectionResult> ProjectAsync(
        UserNotificationRequestedV1 message,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateContract(message, out var kind))
        {
            return new(NotificationProjectionOutcome.IgnoredInvalidContract);
        }

        await using var ownedTransaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var result = await ProjectWithinTransactionAsync(
                message,
                kind,
                cancellationToken);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    private async Task<NotificationProjectionResult> ProjectWithinTransactionAsync(
        UserNotificationRequestedV1 message,
        NotificationKind kind,
        CancellationToken cancellationToken)
    {
        var lockKey = CreateProjectionLockKey(
            message.EventId,
            message.RecipientUserId);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);

        var existingId = await _db.Notifications
            .Where(item => item.UserId == message.RecipientUserId
                && item.EventId == message.EventId)
            .Select(item => (long?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingId is long notificationId)
        {
            return new(
                NotificationProjectionOutcome.AlreadyExists,
                notificationId);
        }

        if (!await _db.Users.AsNoTracking().AnyAsync(
                user => user.Id == message.RecipientUserId,
                cancellationToken))
        {
            return new(NotificationProjectionOutcome.IgnoredMissingOrStaleSource);
        }

        var content = await ResolveContentAsync(message, kind, cancellationToken);
        if (content is null)
        {
            return new(NotificationProjectionOutcome.IgnoredMissingOrStaleSource);
        }

        var preference = await _db.NotificationPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.UserId == message.RecipientUserId
                    && item.Kind == kind,
                cancellationToken);
        var createdAt = _timeProvider.GetUtcNow();
        var normalizedMessage = message.OccurredAt > createdAt
            ? message with { OccurredAt = createdAt }
            : message;
        var notification = _factory.Create(
            normalizedMessage,
            kind,
            content,
            preference,
            createdAt);
        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync(cancellationToken);
        return new(NotificationProjectionOutcome.Created, notification.Id);
    }

    private Task<NotificationContent?> ResolveContentAsync(
        UserNotificationRequestedV1 message,
        NotificationKind kind,
        CancellationToken cancellationToken) => kind switch
    {
        NotificationKind.ReviewAssigned or
        NotificationKind.ReviewDeadlineSoon or
        NotificationKind.ReviewExpired => ResolveReviewAsync(
            message,
            kind,
            cancellationToken),
        NotificationKind.FeedbackCompleted => ResolveFeedbackAsync(
            message,
            cancellationToken),
        NotificationKind.KeyDropOpened or
        NotificationKind.FavoriteGameKeyDropCreated => ResolveKeyDropAsync(
            message,
            kind,
            cancellationToken),
        NotificationKind.FollowedUserGamePublished => ResolveFollowedGameAsync(
            message,
            cancellationToken),
        NotificationKind.AccountStatusChanged => ResolveAccountAsync(
            message,
            cancellationToken),
        // The report source table is introduced with the moderation module. Until then,
        // acknowledging without a notification is safer than trusting an unverifiable ID.
        NotificationKind.ReportResolved => Task.FromResult<NotificationContent?>(null),
        _ => Task.FromResult<NotificationContent?>(null),
    };

    private async Task<NotificationContent?> ResolveReviewAsync(
        UserNotificationRequestedV1 message,
        NotificationKind kind,
        CancellationToken cancellationToken)
    {
        var source = await (
            from assignment in _db.ReviewAssignments.AsNoTracking()
            join game in _db.Games.AsNoTracking()
                on assignment.GameId equals game.Id
            where assignment.Id == message.SourceId
            where assignment.ReviewerId == message.RecipientUserId
            select new { game.Title, assignment.Status })
            .SingleOrDefaultAsync(cancellationToken);
        var expectedStatus = kind == NotificationKind.ReviewExpired
            ? ReviewAssignmentStatus.Expired
            : ReviewAssignmentStatus.Active;
        if (source is null || source.Status != expectedStatus)
        {
            return null;
        }

        return kind switch
        {
            NotificationKind.ReviewAssigned => new(
                "새 리뷰 작업이 배정되었습니다",
                $"‘{source.Title}’ 리뷰를 마감 전까지 작성해 주세요.",
                $"/Reviews/Submit?assignmentId={message.SourceId}"),
            NotificationKind.ReviewDeadlineSoon => new(
                "리뷰 마감이 10분 남았습니다",
                $"‘{source.Title}’ 리뷰 작업의 마감이 임박했습니다.",
                $"/Reviews/Submit?assignmentId={message.SourceId}"),
            NotificationKind.ReviewExpired => new(
                "리뷰 작업이 만료되었습니다",
                $"‘{source.Title}’ 리뷰 작업의 마감 시간이 지났습니다.",
                "/Reviews"),
            _ => null,
        };
    }

    private async Task<NotificationContent?> ResolveFeedbackAsync(
        UserNotificationRequestedV1 message,
        CancellationToken cancellationToken)
    {
        var source = await (
            from submission in _db.FeedbackSubmissions.AsNoTracking()
            join assignment in _db.ReviewAssignments.AsNoTracking()
                on submission.AssignmentId equals assignment.Id
            join request in _db.ReviewRequests.AsNoTracking()
                on assignment.RequestId equals request.Id
            join game in _db.Games.AsNoTracking()
                on request.GameId equals game.Id
            where submission.Id == message.SourceId
            where game.OwnerId == message.RecipientUserId
            select new { game.Id, game.Title })
            .SingleOrDefaultAsync(cancellationToken);
        return source is null
            ? null
            : new NotificationContent(
                "새 피드백이 도착했습니다",
                $"‘{source.Title}’에 새 피드백이 등록되었습니다.",
                $"/Games/Feedback?id={source.Id}");
    }

    private async Task<NotificationContent?> ResolveKeyDropAsync(
        UserNotificationRequestedV1 message,
        NotificationKind kind,
        CancellationToken cancellationToken)
    {
        var source = await (
            from drop in _db.KeyDrops.AsNoTracking()
            join game in _db.Games.AsNoTracking() on drop.GameId equals game.Id
            where drop.Id == message.SourceId
            select new { drop.Id, drop.GameId, game.Title })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return null;
        }

        var hasRelationship = kind == NotificationKind.KeyDropOpened
            ? await _db.KeyDropSubscriptions.AsNoTracking().AnyAsync(
                item => item.UserId == message.RecipientUserId
                    && item.DropId == source.Id,
                cancellationToken)
            : await _db.FavoriteGames.AsNoTracking().AnyAsync(
                item => item.UserId == message.RecipientUserId
                    && item.GameId == source.GameId,
                cancellationToken);
        if (!hasRelationship)
        {
            return null;
        }

        return kind == NotificationKind.KeyDropOpened
            ? new NotificationContent(
                "신청한 키 배포가 시작되었습니다",
                $"‘{source.Title}’의 키 배포가 열렸습니다. 신청은 키 수령을 보장하지 않습니다.",
                $"/KeyDrops/Detail?id={source.Id}")
            : new NotificationContent(
                "관심 게임에 새 키 배포가 등록되었습니다",
                $"‘{source.Title}’의 새 키 배포 일정을 확인해 보세요.",
                $"/KeyDrops/Detail?id={source.Id}");
    }

    private async Task<NotificationContent?> ResolveFollowedGameAsync(
        UserNotificationRequestedV1 message,
        CancellationToken cancellationToken)
    {
        var source = await _db.Games
            .AsNoTracking()
            .Where(game => game.Id == message.SourceId
                && game.OwnerId != message.RecipientUserId)
            .Select(game => new { game.Id, game.OwnerId, game.Title })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null
            || !await _db.UserFollows.AsNoTracking().AnyAsync(
                item => item.FollowerId == message.RecipientUserId
                    && item.FollowedUserId == source.OwnerId,
                cancellationToken))
        {
            return null;
        }

        return new(
            "팔로우한 사용자가 새 게임을 공개했습니다",
            $"‘{source.Title}’ 게임이 새로 등록되었습니다.",
            $"/Games/Detail?id={source.Id}");
    }

    private async Task<NotificationContent?> ResolveAccountAsync(
        UserNotificationRequestedV1 message,
        CancellationToken cancellationToken)
    {
        if (message.SourceId != message.RecipientUserId
            || !await _db.Users.AsNoTracking().AnyAsync(
                user => user.Id == message.SourceId,
                cancellationToken))
        {
            return null;
        }

        return new(
            "계정 상태가 변경되었습니다",
            "계정의 현재 상태와 이용 가능 기능을 확인해 주세요.",
            "/Account/Profile");
    }

    private static bool TryValidateContract(
        UserNotificationRequestedV1 message,
        out NotificationKind kind)
    {
        kind = default;
        return message.EventId != Guid.Empty
            && message.RecipientUserId > 0
            && message.SourceId > 0
            && message.OccurredAt != default
            && Enum.TryParse(message.Kind, ignoreCase: false, out kind)
            && Enum.IsDefined(kind)
            && string.Equals(
                message.SourceType,
                NotificationSourceTypes.For(kind),
                StringComparison.Ordinal);
    }

    private static long CreateProjectionLockKey(Guid eventId, long recipientUserId)
    {
        Span<byte> eventBytes = stackalloc byte[16];
        eventId.TryWriteBytes(eventBytes);
        return BinaryPrimitives.ReadInt64LittleEndian(eventBytes)
            ^ BinaryPrimitives.ReadInt64LittleEndian(eventBytes[8..])
            ^ recipientUserId;
    }
}
