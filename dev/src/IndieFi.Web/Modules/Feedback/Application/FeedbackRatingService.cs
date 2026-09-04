using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace IndieFi.Web.Modules.Feedback.Application;

public enum FeedbackRatingSaveOutcome
{
    Created,
    Updated,
    Unchanged,
    Invalid,
    NotFound,
    WindowClosed,
    Conflict,
    AccountUnavailable,
}

public sealed record FeedbackRatingSaveResult(
    FeedbackRatingSaveOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations,
    long? RatingId = null,
    int? Revision = null,
    DateTimeOffset? RatingClosesAt = null);

public sealed class FeedbackRatingService
{
    public const string ScoreField = "Score";
    public const string ReasonField = "Reason";
    public const string RevisionField = "Revision";

    private static readonly string FindTargetSql = $"""
        SELECT submission.id AS "FeedbackSubmissionId",
               submission.reviewer_id AS "ReviewerId",
               submission.created_at
                   + interval '{FeedbackQualityPolicy.RatingWindowDays} days'
                   AS "RatingClosesAt",
               now() <= submission.created_at
                   + interval '{FeedbackQualityPolicy.RatingWindowDays} days'
                   AS "WindowOpen"
          FROM feedback_submissions AS submission
          JOIN review_assignments AS assignment
            ON assignment.id = submission.assignment_id
          JOIN review_requests AS request
            ON request.id = assignment.request_id
         WHERE submission.id = @feedbackId
           AND request.requester_id = @raterId
        """;

    private static readonly string CreateSql = $"""
        INSERT INTO feedback_ratings (
            feedback_submission_id,
            rater_id,
            reviewer_id,
            score,
            reason)
        SELECT submission.id,
               @raterId,
               submission.reviewer_id,
               @score,
               @reason
          FROM feedback_submissions AS submission
          JOIN review_assignments AS assignment
            ON assignment.id = submission.assignment_id
          JOIN review_requests AS request
            ON request.id = assignment.request_id
         WHERE submission.id = @feedbackId
           AND request.requester_id = @raterId
           AND now() <= submission.created_at
               + interval '{FeedbackQualityPolicy.RatingWindowDays} days'
        ON CONFLICT (feedback_submission_id) DO NOTHING
        RETURNING id AS "RatingId", revision AS "Revision"
        """;

    private static readonly string UpdateSql = $"""
        UPDATE feedback_ratings AS rating
           SET score = @score,
               reason = @reason,
               revision = rating.revision + 1,
               updated_at = now()
          FROM feedback_submissions AS submission
          JOIN review_assignments AS assignment
            ON assignment.id = submission.assignment_id
          JOIN review_requests AS request
            ON request.id = assignment.request_id
         WHERE rating.feedback_submission_id = @feedbackId
           AND rating.feedback_submission_id = submission.id
           AND rating.rater_id = @raterId
           AND request.requester_id = @raterId
           AND rating.revision = @expectedRevision
           AND now() <= submission.created_at
               + interval '{FeedbackQualityPolicy.RatingWindowDays} days'
        RETURNING rating.id AS "RatingId", rating.revision AS "Revision"
        """;

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

    private readonly AppDbContext _db;
    private readonly ILogger<FeedbackRatingService> _logger;

    public FeedbackRatingService(
        AppDbContext db,
        ILogger<FeedbackRatingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<FeedbackRatingSaveResult> SaveAsync(
        long feedbackId,
        long raterId,
        int score,
        string? reasonInput,
        int? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(feedbackId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(raterId);

        var violations = ValidateInput(
            score,
            reasonInput,
            expectedRevision,
            out var reason);
        if (violations.Count != 0)
        {
            return Result(
                FeedbackRatingSaveOutcome.Invalid,
                feedbackId,
                raterId,
                violations);
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                raterId,
                cancellationToken))
        {
            return await CommitAsync(
                transaction,
                Result(
                    FeedbackRatingSaveOutcome.AccountUnavailable,
                    feedbackId,
                    raterId),
                cancellationToken);
        }

        var target = await FindTargetAsync(
            feedbackId,
            raterId,
            cancellationToken);
        if (target is null)
        {
            return await CommitAsync(
                transaction,
                Result(
                    FeedbackRatingSaveOutcome.NotFound,
                    feedbackId,
                    raterId),
                cancellationToken);
        }

        var current = await FindCurrentAsync(feedbackId, cancellationToken);
        if (current is not null && Matches(current, score, reason))
        {
            return await CommitAsync(
                transaction,
                Success(
                    FeedbackRatingSaveOutcome.Unchanged,
                    current,
                    target.RatingClosesAt,
                    feedbackId,
                    raterId),
                cancellationToken);
        }

        if (!target.WindowOpen)
        {
            return await CommitAsync(
                transaction,
                Result(
                    FeedbackRatingSaveOutcome.WindowClosed,
                    feedbackId,
                    raterId,
                    ratingId: current?.Id,
                    revision: current?.Revision,
                    ratingClosesAt: target.RatingClosesAt),
                cancellationToken);
        }

        if (current is null && expectedRevision is not null)
        {
            return await CommitAsync(
                transaction,
                Result(
                    FeedbackRatingSaveOutcome.Conflict,
                    feedbackId,
                    raterId,
                    ratingClosesAt: target.RatingClosesAt),
                cancellationToken);
        }

        if (current is not null && expectedRevision is null)
        {
            return await CommitAsync(
                transaction,
                Result(
                    FeedbackRatingSaveOutcome.Conflict,
                    feedbackId,
                    raterId,
                    ratingId: current.Id,
                    revision: current.Revision,
                    ratingClosesAt: target.RatingClosesAt),
                cancellationToken);
        }

        var mutation = current is null
            ? await CreateAsync(
                feedbackId,
                raterId,
                score,
                reason,
                cancellationToken)
            : await UpdateAsync(
                feedbackId,
                raterId,
                score,
                reason,
                expectedRevision!.Value,
                cancellationToken);
        if (mutation is not null)
        {
            var outcome = current is null
                ? FeedbackRatingSaveOutcome.Created
                : FeedbackRatingSaveOutcome.Updated;
            return await CommitAsync(
                transaction,
                Result(
                    outcome,
                    feedbackId,
                    raterId,
                    ratingId: mutation.RatingId,
                    revision: mutation.Revision,
                    ratingClosesAt: target.RatingClosesAt),
                cancellationToken);
        }

        var refreshed = await FindCurrentAsync(feedbackId, cancellationToken);
        if (refreshed is not null && Matches(refreshed, score, reason))
        {
            return await CommitAsync(
                transaction,
                Success(
                    FeedbackRatingSaveOutcome.Unchanged,
                    refreshed,
                    target.RatingClosesAt,
                    feedbackId,
                    raterId),
                cancellationToken);
        }

        var refreshedTarget = await FindTargetAsync(
            feedbackId,
            raterId,
            cancellationToken);
        var finalOutcome = refreshedTarget is null
            ? FeedbackRatingSaveOutcome.NotFound
            : refreshedTarget.WindowOpen
                ? FeedbackRatingSaveOutcome.Conflict
                : FeedbackRatingSaveOutcome.WindowClosed;

        return await CommitAsync(
            transaction,
            Result(
                finalOutcome,
                feedbackId,
                raterId,
                ratingId: refreshed?.Id,
                revision: refreshed?.Revision,
                ratingClosesAt: refreshedTarget?.RatingClosesAt
                    ?? target.RatingClosesAt),
            cancellationToken);
    }

    private static IReadOnlyList<ValidationViolation> ValidateInput(
        int score,
        string? reasonInput,
        int? expectedRevision,
        out string? reason)
    {
        var violations = new List<ValidationViolation>();
        if (!FeedbackQualityPolicy.IsValidScore(score))
        {
            violations.Add(new ValidationViolation(
                ScoreField,
                $"평가는 {FeedbackQualityPolicy.MinimumScore}점부터 "
                + $"{FeedbackQualityPolicy.MaximumScore}점까지 선택해 주세요."));
        }

        if (!FeedbackQualityPolicy.TryNormalizeReason(reasonInput, out reason))
        {
            violations.Add(new ValidationViolation(
                ReasonField,
                $"평가 사유는 {FeedbackQualityPolicy.MaximumReasonLength}자 이하여야 합니다."));
        }

        if (expectedRevision is <= 0)
        {
            violations.Add(new ValidationViolation(
                RevisionField,
                "평가 버전이 올바르지 않습니다."));
        }

        return violations;
    }

    private async Task<FeedbackRatingTarget?> FindTargetAsync(
        long feedbackId,
        long raterId,
        CancellationToken cancellationToken)
    {
        var targets = await _db.Database.SqlQueryRaw<FeedbackRatingTarget>(
                FindTargetSql,
                new NpgsqlParameter("feedbackId", feedbackId),
                new NpgsqlParameter("raterId", raterId))
            .ToListAsync(cancellationToken);
        return targets.SingleOrDefault();
    }

    private Task<FeedbackRating?> FindCurrentAsync(
        long feedbackId,
        CancellationToken cancellationToken) =>
        _db.FeedbackRatings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                rating => rating.FeedbackSubmissionId == feedbackId,
                cancellationToken);

    private async Task<FeedbackRatingMutation?> CreateAsync(
        long feedbackId,
        long raterId,
        int score,
        string? reason,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQueryRaw<FeedbackRatingMutation>(
                CreateSql,
                new NpgsqlParameter("feedbackId", feedbackId),
                new NpgsqlParameter("raterId", raterId),
                new NpgsqlParameter("score", score),
                new NpgsqlParameter<string?>("reason", reason))
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    private async Task<FeedbackRatingMutation?> UpdateAsync(
        long feedbackId,
        long raterId,
        int score,
        string? reason,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Database.SqlQueryRaw<FeedbackRatingMutation>(
                UpdateSql,
                new NpgsqlParameter("feedbackId", feedbackId),
                new NpgsqlParameter("raterId", raterId),
                new NpgsqlParameter("score", score),
                new NpgsqlParameter<string?>("reason", reason),
                new NpgsqlParameter("expectedRevision", expectedRevision))
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    private static bool Matches(
        FeedbackRating rating,
        int score,
        string? reason) =>
        rating.Score == score
        && string.Equals(rating.Reason, reason, StringComparison.Ordinal);

    private FeedbackRatingSaveResult Success(
        FeedbackRatingSaveOutcome outcome,
        FeedbackRating rating,
        DateTimeOffset closesAt,
        long feedbackId,
        long raterId) =>
        Result(
            outcome,
            feedbackId,
            raterId,
            ratingId: rating.Id,
            revision: rating.Revision,
            ratingClosesAt: closesAt);

    private async Task<FeedbackRatingSaveResult> CommitAsync(
        IDbContextTransaction? transaction,
        FeedbackRatingSaveResult result,
        CancellationToken cancellationToken)
    {
        await AccountMutationGuard.CommitIfOwnedAsync(
            transaction,
            cancellationToken);
        return result;
    }

    private FeedbackRatingSaveResult Result(
        FeedbackRatingSaveOutcome outcome,
        long feedbackId,
        long raterId,
        IReadOnlyList<ValidationViolation>? violations = null,
        long? ratingId = null,
        int? revision = null,
        DateTimeOffset? ratingClosesAt = null)
    {
        _logger.LogInformation(
            "FeedbackRating save outcome={Outcome} feedback={FeedbackId} rater={RaterId} revision={Revision}",
            outcome,
            feedbackId,
            raterId,
            revision);
        return new FeedbackRatingSaveResult(
            outcome,
            violations ?? NoViolations,
            ratingId,
            revision,
            ratingClosesAt);
    }

    private sealed record FeedbackRatingTarget(
        long FeedbackSubmissionId,
        long ReviewerId,
        DateTimeOffset RatingClosesAt,
        bool WindowOpen);

    private sealed record FeedbackRatingMutation(long RatingId, int Revision);
}
