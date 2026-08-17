using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Media.Application;

public enum MediaUploadReservationOutcome
{
    Reserved,
    InvalidUpload,
    AccountUnavailable,
    TargetNotFound,
    PendingLimitReached,
    AttachmentLimitReached,
    ReplacementInProgress,
    CapacityExceeded,
}

public sealed record MediaUploadReservationResult(
    MediaUploadReservationOutcome Outcome,
    Guid? MediaId = null,
    DateTimeOffset? UploadExpiresAt = null);

public sealed class MediaUploadReservationService
{
    private static readonly MediaStatus[] CapacityStatuses =
    [
        MediaStatus.UploadPending,
        MediaStatus.ProcessingPending,
        MediaStatus.Processing,
        MediaStatus.Ready,
    ];

    private static readonly MediaStatus[] CandidateStatuses =
    [
        MediaStatus.UploadPending,
        MediaStatus.ProcessingPending,
        MediaStatus.Processing,
    ];

    private readonly AppDbContext _db;

    public MediaUploadReservationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<MediaUploadReservationResult> ReserveAsync(
        long ownerUserId,
        MediaTarget target,
        MediaAttachmentKind attachmentKind,
        string? declaredContentType,
        long declaredBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerUserId);
        ArgumentNullException.ThrowIfNull(target);
        MediaPolicy.RequireCompatible(target, attachmentKind);

        var imageLimits = MediaPolicy.GetImageLimits(attachmentKind);
        if (!MediaObjectAccessPolicy.IsAllowedOriginalContentType(declaredContentType)
            || declaredBytes <= 0
            || declaredBytes > imageLimits.MaximumOriginalBytes)
        {
            return new MediaUploadReservationResult(
                MediaUploadReservationOutcome.InvalidUpload);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);

        // Every reservation for an account locks the account first. This is the
        // serialization point for the account-wide UploadPending count, even when
        // concurrent requests target different games or a profile.
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                ownerUserId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadReservationResult(
                MediaUploadReservationOutcome.AccountUnavailable);
        }

        // Lock the concrete target second when it exists so every media write uses
        // one lock order. A missing self-owned profile is created under the account
        // lock below. Owner filtering intentionally makes a foreign game
        // indistinguishable from a missing one.
        if (!await LockOwnedTargetAsync(
                ownerUserId,
                target,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadReservationResult(
                MediaUploadReservationOutcome.TargetNotFound);
        }

        var pendingCount = await _db.MediaAssets.CountAsync(
            item => item.OwnerUserId == ownerUserId
                && item.Status == MediaStatus.UploadPending,
            cancellationToken);
        if (pendingCount >= MediaPolicy.MaximumUploadPendingPerAccount)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadReservationResult(
                MediaUploadReservationOutcome.PendingLimitReached);
        }

        var targetMedia = TargetMedia(target);
        if (MediaPolicy.UsesSingleCurrentSlot(attachmentKind)
            && await targetMedia.AnyAsync(
                item => item.AttachmentKind == attachmentKind
                    && CandidateStatuses.Contains(item.Status),
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadReservationResult(
                MediaUploadReservationOutcome.ReplacementInProgress);
        }

        if (attachmentKind == MediaAttachmentKind.GameScreenshot)
        {
            var attachedCount = await targetMedia.CountAsync(
                item => item.AttachmentKind == attachmentKind
                    && CapacityStatuses.Contains(item.Status),
                cancellationToken);
            if (attachedCount >= MediaPolicy.GameScreenshotMaximumCount)
            {
                await AccountMutationGuard.CommitIfOwnedAsync(
                    transaction,
                    cancellationToken);
                return new MediaUploadReservationResult(
                    MediaUploadReservationOutcome.AttachmentLimitReached);
            }
        }

        // A singleton replacement consumes the old media's slot rather than an
        // additional one. Other active candidates still reserve their declared size.
        var capacityRows = targetMedia.Where(
            item => CapacityStatuses.Contains(item.Status));
        if (MediaPolicy.UsesSingleCurrentSlot(attachmentKind))
        {
            capacityRows = capacityRows.Where(
                item => !(item.AttachmentKind == attachmentKind && item.IsCurrent));
        }

        var reservedBytes = await capacityRows
            .SumAsync(item => (long?)item.DeclaredBytes, cancellationToken)
            ?? 0;
        var maximumActiveBytes = MediaPolicy.GetMaximumActiveOriginalBytes(target.Kind);
        if (reservedBytes > maximumActiveBytes
            || declaredBytes > maximumActiveBytes - reservedBytes)
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return new MediaUploadReservationResult(
                MediaUploadReservationOutcome.CapacityExceeded);
        }

        if (target.Kind == MediaTargetKind.Profile
            && !await _db.UserProfiles.AnyAsync(
                profile => profile.UserId == target.Id,
                cancellationToken))
        {
            // The account row locked above serializes this creation with profile
            // edits and other media reservations. The profile and reservation are
            // persisted by the same SaveChanges/transaction below.
            _db.UserProfiles.Add(new UserProfile { UserId = target.Id });
        }

        var media = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            GameId = target.Kind == MediaTargetKind.Game ? target.Id : null,
            ProfileUserId = target.Kind == MediaTargetKind.Profile ? target.Id : null,
            AttachmentKind = attachmentKind,
            DeclaredContentType = declaredContentType!,
            DeclaredBytes = declaredBytes,
        };
        _db.MediaAssets.Add(media);
        await _db.SaveChangesAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(transaction, cancellationToken);

        return new MediaUploadReservationResult(
            MediaUploadReservationOutcome.Reserved,
            media.Id,
            media.UploadExpiresAt);
    }

    private IQueryable<MediaAsset> TargetMedia(MediaTarget target) =>
        target.Kind switch
        {
            MediaTargetKind.Game => _db.MediaAssets.Where(
                item => item.GameId == target.Id),
            MediaTargetKind.Profile => _db.MediaAssets.Where(
                item => item.ProfileUserId == target.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    private async Task<bool> LockOwnedTargetAsync(
        long ownerUserId,
        MediaTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Kind == MediaTargetKind.Profile)
        {
            if (target.Id != ownerUserId)
            {
                return false;
            }

            await _db.UserProfiles
                .FromSqlInterpolated(
                    $"SELECT * FROM user_profiles WHERE user_id = {target.Id} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            // A missing row is a valid empty profile for its owning account. The
            // locked account row is the serialization point until it is created.
            return true;
        }

        if (target.Kind == MediaTargetKind.Game)
        {
            var games = await _db.Games
                .FromSqlInterpolated(
                    $"SELECT * FROM games WHERE id = {target.Id} AND owner_id = {ownerUserId} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            return games.Count == 1;
        }

        throw new ArgumentOutOfRangeException(nameof(target));
    }
}
