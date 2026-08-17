using IndieFi.Web.Application.Abstractions;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;

namespace IndieFi.Web.Modules.Media;

public sealed record MediaReservationRequest(
    string? TargetKind,
    long TargetId,
    string? AttachmentKind,
    string? ContentType,
    long ContentLength);

public sealed record MediaReservationResponse(
    Guid MediaId,
    string Status,
    DateTimeOffset UploadExpiresAt);

public sealed record MediaUploadAttemptResponse(
    Guid MediaId,
    Guid UploadAttemptId,
    Uri UploadUrl,
    DateTimeOffset UrlExpiresAt,
    string RequiredContentType,
    long RequiredContentLength);

public sealed record MediaProcessingAcceptedResponse(
    Guid MediaId,
    Guid ProcessingEventId,
    string Code);

public sealed record MediaDeletionAcceptedResponse(
    Guid MediaId,
    Guid DeletionEventId,
    string Code);

public sealed record MediaStatusResponse(
    Guid MediaId,
    string AttachmentKind,
    string Status,
    bool IsCurrent,
    int? SortOrder,
    int ProcessingAttemptCount,
    string? FailureCode,
    DateTimeOffset UploadExpiresAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset UpdatedAt);

public sealed record MediaAntiforgeryResponse(
    string RequestToken,
    string HeaderName);

public sealed record GameScreenshotOrderRequest(
    IReadOnlyList<Guid>? MediaIds);

public sealed record GameScreenshotOrderResponse(
    long GameId,
    IReadOnlyList<Guid> MediaIds);

public sealed record MediaApiError(string Code, string Message);

public static class MediaEndpoints
{
    private const string StatusPath = "/api/media/{0}";

    public static IEndpointRouteBuilder MapMediaEndpoints(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetValue<bool>(
                $"{MediaObjectStorageOptions.SectionName}:Enabled"))
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/media")
            .RequireAuthorization()
            .AddEndpointFilter<MediaApiSecurityFilter>();

        group.MapGet("/antiforgery", IssueAntiforgeryToken);
        group.MapPost("/reservations", ReserveAsync);
        group.MapPost("/{mediaId:guid}/upload-attempts", IssueUploadUrlAsync);
        group.MapPost(
            "/{mediaId:guid}/upload-attempts/{uploadAttemptId:guid}/complete",
            CompleteAsync);
        group.MapGet("/{mediaId:guid}", FindStatusAsync);
        group.MapDelete("/{mediaId:guid}", RemoveAsync);
        group.MapPut(
            "/games/{gameId:long}/screenshots/order",
            ReorderScreenshotsAsync);

        return endpoints;
    }

    private static IResult IssueAntiforgeryToken(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        if (tokens.RequestToken is null || tokens.HeaderName is null)
        {
            throw new InvalidOperationException(
                "Antiforgery request-token generation is unavailable.");
        }

        return Results.Ok(new MediaAntiforgeryResponse(
            tokens.RequestToken,
            tokens.HeaderName));
    }

    private static async Task<IResult> ReserveAsync(
        MediaReservationRequest request,
        ICurrentUser currentUser,
        MediaUploadReservationService reservationService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(currentUser, out var ownerUserId))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required.");
        }

        if (!TryParseTarget(request, out var target, out var attachmentKind))
        {
            return Error(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_media_reservation",
                "The media target or attachment kind is invalid.");
        }

        var result = await reservationService.ReserveAsync(
            ownerUserId,
            target!,
            attachmentKind,
            request.ContentType,
            request.ContentLength,
            cancellationToken);
        return result.Outcome switch
        {
            MediaUploadReservationOutcome.Reserved => Results.Created(
                string.Format(StatusPath, result.MediaId),
                new MediaReservationResponse(
                    result.MediaId!.Value,
                    MediaStatus.UploadPending.ToString(),
                    result.UploadExpiresAt!.Value)),
            MediaUploadReservationOutcome.InvalidUpload => Error(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_upload",
                "The declared media type or size is invalid."),
            MediaUploadReservationOutcome.AccountUnavailable => Error(
                StatusCodes.Status403Forbidden,
                "account_unavailable",
                "The account cannot mutate media."),
            MediaUploadReservationOutcome.TargetNotFound => NotFound(),
            MediaUploadReservationOutcome.PendingLimitReached => Conflict(
                "pending_upload_limit_reached",
                "The account has too many pending media uploads."),
            MediaUploadReservationOutcome.AttachmentLimitReached => Conflict(
                "attachment_limit_reached",
                "The target has reached its media attachment limit."),
            MediaUploadReservationOutcome.ReplacementInProgress => Conflict(
                "replacement_in_progress",
                "Another replacement is already in progress."),
            MediaUploadReservationOutcome.CapacityExceeded => Conflict(
                "media_capacity_exceeded",
                "The target has insufficient media capacity."),
            _ => throw new InvalidOperationException(
                "The reservation service returned an unknown outcome."),
        };
    }

    private static async Task<IResult> IssueUploadUrlAsync(
        Guid mediaId,
        ICurrentUser currentUser,
        MediaUploadUrlService uploadUrlService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(currentUser, out var ownerUserId))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required.");
        }

        if (mediaId == Guid.Empty)
        {
            return NotFound();
        }

        MediaUploadUrlResult result;
        try
        {
            result = await uploadUrlService.IssueAsync(
                ownerUserId,
                mediaId,
                cancellationToken);
        }
        catch (MediaObjectStorageException)
        {
            return StorageUnavailable();
        }

        return result.Outcome switch
        {
            MediaUploadUrlOutcome.Issued => Results.Created(
                string.Format(StatusPath, mediaId),
                new MediaUploadAttemptResponse(
                    mediaId,
                    result.UploadAttemptId!.Value,
                    result.UploadUrl!,
                    result.UrlExpiresAt!.Value,
                    result.RequiredContentType!,
                    result.RequiredContentLength!.Value)),
            MediaUploadUrlOutcome.AccountUnavailable => Error(
                StatusCodes.Status403Forbidden,
                "account_unavailable",
                "The account cannot mutate media."),
            MediaUploadUrlOutcome.MediaNotFound => NotFound(),
            MediaUploadUrlOutcome.MediaNotPending => Conflict(
                "media_not_upload_pending",
                "The media reservation no longer accepts uploads."),
            MediaUploadUrlOutcome.ReservationExpired => Conflict(
                "media_reservation_expired",
                "The media reservation has expired."),
            MediaUploadUrlOutcome.ShortWindowLimitReached => Error(
                StatusCodes.Status429TooManyRequests,
                "upload_short_window_limit_reached",
                "The short-window upload URL issue limit was reached."),
            MediaUploadUrlOutcome.DailyLimitReached => Error(
                StatusCodes.Status429TooManyRequests,
                "upload_daily_limit_reached",
                "The daily upload URL issue limit was reached."),
            MediaUploadUrlOutcome.RateLimiterUnavailable => Error(
                StatusCodes.Status503ServiceUnavailable,
                "upload_rate_limiter_unavailable",
                "Upload URL issuing is temporarily unavailable."),
            _ => throw new InvalidOperationException(
                "The upload URL service returned an unknown outcome."),
        };
    }

    private static async Task<IResult> CompleteAsync(
        Guid mediaId,
        Guid uploadAttemptId,
        ICurrentUser currentUser,
        MediaUploadCompletionService completionService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(currentUser, out var ownerUserId))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required.");
        }

        if (mediaId == Guid.Empty || uploadAttemptId == Guid.Empty)
        {
            return NotFound();
        }

        MediaUploadCompletionResult result;
        try
        {
            result = await completionService.CompleteAsync(
                ownerUserId,
                mediaId,
                uploadAttemptId,
                cancellationToken);
        }
        catch (MediaObjectStorageException)
        {
            return StorageUnavailable();
        }

        return result.Outcome switch
        {
            MediaUploadCompletionOutcome.Completed
                or MediaUploadCompletionOutcome.AlreadyCompleted =>
                Results.Accepted(
                    string.Format(StatusPath, mediaId),
                    new MediaProcessingAcceptedResponse(
                        mediaId,
                        result.ProcessingEventId!.Value,
                        "processing_requested")),
            MediaUploadCompletionOutcome.UploadRejected => Error(
                StatusCodes.Status422UnprocessableEntity,
                "upload_metadata_mismatch",
                "The uploaded object does not match its declaration."),
            MediaUploadCompletionOutcome.AccountUnavailable => Error(
                StatusCodes.Status403Forbidden,
                "account_unavailable",
                "The account cannot mutate media."),
            MediaUploadCompletionOutcome.MediaNotFound
                or MediaUploadCompletionOutcome.UploadAttemptNotFound => NotFound(),
            MediaUploadCompletionOutcome.UploadAttemptSuperseded => Conflict(
                "upload_attempt_superseded",
                "The upload attempt has been superseded."),
            MediaUploadCompletionOutcome.MediaNotPending => Conflict(
                "media_not_upload_pending",
                "The media reservation no longer accepts completion."),
            MediaUploadCompletionOutcome.ReservationExpired => Conflict(
                "media_reservation_expired",
                "The media reservation has expired."),
            MediaUploadCompletionOutcome.StagingObjectNotFound => Conflict(
                "staging_object_not_found",
                "The uploaded staging object was not found."),
            MediaUploadCompletionOutcome.StagingObjectChanged => Conflict(
                "staging_object_changed",
                "The uploaded staging object changed during completion."),
            MediaUploadCompletionOutcome.StorageResponseInvalid =>
                StorageUnavailable(),
            MediaUploadCompletionOutcome.SourceObjectUnavailable =>
                StorageUnavailable(),
            MediaUploadCompletionOutcome.SourceObjectConflict => Conflict(
                "source_object_conflict",
                "The immutable source object conflicts with the reservation."),
            _ => throw new InvalidOperationException(
                "The upload completion service returned an unknown outcome."),
        };
    }

    private static async Task<IResult> FindStatusAsync(
        Guid mediaId,
        ICurrentUser currentUser,
        MediaStatusQueryService statusService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(currentUser, out var ownerUserId))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required.");
        }

        if (mediaId == Guid.Empty)
        {
            return NotFound();
        }

        var status = await statusService.FindAsync(
            ownerUserId,
            mediaId,
            cancellationToken);
        if (status is null)
        {
            return NotFound();
        }

        return Results.Ok(new MediaStatusResponse(
            status.MediaId,
            status.AttachmentKind.ToString(),
            status.Status.ToString(),
            status.IsCurrent,
            status.SortOrder,
            status.ProcessingAttemptCount,
            status.FailureCode,
            status.UploadExpiresAt,
            status.PublishedAt,
            status.UpdatedAt));
    }

    private static async Task<IResult> RemoveAsync(
        Guid mediaId,
        ICurrentUser currentUser,
        MediaPublicationService publicationService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(currentUser, out var ownerUserId))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required.");
        }

        if (mediaId == Guid.Empty)
        {
            return NotFound();
        }

        var result = await publicationService.RemoveAsync(
            ownerUserId,
            mediaId,
            cancellationToken);
        return result.Outcome switch
        {
            MediaRemovalOutcome.Removed or MediaRemovalOutcome.AlreadyRemoved =>
                Results.Accepted(
                    string.Format(StatusPath, mediaId),
                    new MediaDeletionAcceptedResponse(
                        mediaId,
                        result.DeletionEventId!.Value,
                        "deletion_requested")),
            MediaRemovalOutcome.AccountUnavailable => Error(
                StatusCodes.Status403Forbidden,
                "account_unavailable",
                "The account cannot mutate media."),
            MediaRemovalOutcome.MediaNotFound => NotFound(),
            MediaRemovalOutcome.NotRemovable => Conflict(
                "media_not_removable",
                "The media cannot be removed from its current state."),
            _ => throw new InvalidOperationException(
                "The publication service returned an unknown outcome."),
        };
    }

    private static async Task<IResult> ReorderScreenshotsAsync(
        long gameId,
        GameScreenshotOrderRequest request,
        ICurrentUser currentUser,
        GameScreenshotOrderService orderService,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnerId(currentUser, out var ownerUserId))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "authentication_required",
                "Authentication is required.");
        }

        if (gameId <= 0)
        {
            return NotFound();
        }

        if (request.MediaIds is null)
        {
            return Error(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_screenshot_order",
                "The complete ready screenshot order is required.");
        }

        var result = await orderService.ReorderAsync(
            ownerUserId,
            gameId,
            request.MediaIds,
            cancellationToken);
        return result.Outcome switch
        {
            GameScreenshotOrderOutcome.Reordered => Results.Ok(
                new GameScreenshotOrderResponse(
                    gameId,
                    result.MediaIds!)),
            GameScreenshotOrderOutcome.AccountUnavailable => Error(
                StatusCodes.Status403Forbidden,
                "account_unavailable",
                "The account cannot mutate media."),
            GameScreenshotOrderOutcome.GameNotFound => NotFound(),
            GameScreenshotOrderOutcome.InvalidOrder => Error(
                StatusCodes.Status422UnprocessableEntity,
                "invalid_screenshot_order",
                "The submitted order must contain every ready screenshot exactly once."),
            _ => throw new InvalidOperationException(
                "The screenshot order service returned an unknown outcome."),
        };
    }

    private static bool TryParseTarget(
        MediaReservationRequest request,
        out MediaTarget? target,
        out MediaAttachmentKind attachmentKind)
    {
        target = null;
        attachmentKind = default;
        if (request.TargetId <= 0
            || !TryParseNamedEnum(request.TargetKind, out MediaTargetKind targetKind)
            || !TryParseNamedEnum(request.AttachmentKind, out attachmentKind)
            || MediaPolicy.GetTargetKind(attachmentKind) != targetKind)
        {
            return false;
        }

        target = targetKind switch
        {
            MediaTargetKind.Game => MediaTarget.Game(request.TargetId),
            MediaTargetKind.Profile => MediaTarget.Profile(request.TargetId),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return true;
    }

    private static bool TryParseNamedEnum<TEnum>(
        string? value,
        out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        return value is not null
            && Enum.GetNames<TEnum>().Any(name => string.Equals(
                name,
                value,
                StringComparison.OrdinalIgnoreCase))
            && Enum.TryParse(value, ignoreCase: true, out result);
    }

    private static bool TryGetOwnerId(ICurrentUser currentUser, out long ownerUserId)
    {
        ownerUserId = currentUser.UserId ?? 0;
        return currentUser.IsAuthenticated && ownerUserId > 0;
    }

    private static IResult NotFound() => Error(
        StatusCodes.Status404NotFound,
        "media_not_found",
        "The media resource was not found.");

    private static IResult Conflict(string code, string message) => Error(
        StatusCodes.Status409Conflict,
        code,
        message);

    private static IResult StorageUnavailable() => Error(
        StatusCodes.Status503ServiceUnavailable,
        "media_storage_unavailable",
        "Media object storage is temporarily unavailable.");

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(
            new MediaApiError(code, message),
            statusCode: statusCode);

    private sealed class MediaApiSecurityFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(
            EndpointFilterInvocationContext context,
            EndpointFilterDelegate next)
        {
            var httpContext = context.HttpContext;
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers["Referrer-Policy"] = "no-referrer";

            if (!HttpMethods.IsGet(httpContext.Request.Method)
                && !HttpMethods.IsHead(httpContext.Request.Method)
                && !HttpMethods.IsOptions(httpContext.Request.Method))
            {
                try
                {
                    await httpContext.RequestServices
                        .GetRequiredService<IAntiforgery>()
                        .ValidateRequestAsync(httpContext);
                }
                catch (AntiforgeryValidationException)
                {
                    return Error(
                        StatusCodes.Status400BadRequest,
                        "antiforgery_validation_failed",
                        "The antiforgery token is missing or invalid.");
                }
            }

            return await next(context);
        }
    }
}
