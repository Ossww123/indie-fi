using System.Diagnostics;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Feedback.Domain;
using IndieFi.Web.Modules.Feedback.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.Feedback.Application;

public sealed record ReviewerReputationSnapshot(
    long ReviewerId,
    int CompletedReviewCount,
    int ExpiredAssignmentCount,
    int RatingCount,
    int RatingScoreSum,
    int Revision,
    DateTimeOffset UpdatedAt,
    decimal QualityScore,
    decimal CompletionReliabilityScore,
    decimal TrustScore,
    ReviewerGrade Grade);

public sealed class ReviewerReputationService
{
    private const string RefreshSql =
        "SELECT refresh_reviewer_reputation(@reviewerId)";

    private const string CandidateReviewerSql = """
        SELECT reviewer_id AS "Value"
          FROM (
                SELECT reviewer_id FROM review_assignments
                UNION
                SELECT reviewer_id FROM feedback_ratings
                UNION
                SELECT reviewer_id FROM reviewer_reputations
               ) AS candidates
         ORDER BY reviewer_id
        """;

    private readonly AppDbContext _db;
    private readonly ILogger<ReviewerReputationService> _logger;

    public ReviewerReputationService(
        AppDbContext db,
        ILogger<ReviewerReputationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReviewerReputationSnapshot?> GetAsync(
        long reviewerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reviewerId);

        var stored = await _db.ReviewerReputations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                reputation => reputation.ReviewerId == reviewerId,
                cancellationToken);
        return stored is null ? null : CreateSnapshot(stored);
    }

    public async Task<ReviewerReputationSnapshot?> RecalculateAsync(
        long reviewerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reviewerId);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = await RecalculateCoreAsync(reviewerId, cancellationToken);
            var processedCount = result.Snapshot is null ? 0 : 1;
            var repairedCount = result.Snapshot is not null && result.Repaired
                ? 1
                : 0;
            FeedbackTelemetry.RecordReviewerRecalculation(
                ReviewerRecalculationOperation.Single,
                ReviewerRecalculationOutcome.Completed,
                processedCount,
                repairedCount,
                Stopwatch.GetElapsedTime(startedAt));

            _logger.LogInformation(
                "ReviewerReputation recalculated outcome={Outcome} reviewer={ReviewerId} revision={Revision}",
                result.Snapshot is null
                    ? "missing"
                    : result.Repaired
                        ? "repaired"
                        : "unchanged",
                reviewerId,
                result.Snapshot?.Revision);
            return result.Snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FeedbackTelemetry.RecordReviewerRecalculation(
                ReviewerRecalculationOperation.Single,
                ReviewerRecalculationOutcome.Cancelled,
                processedCount: 0,
                repairedCount: 0,
                Stopwatch.GetElapsedTime(startedAt));
            _logger.LogWarning(
                "ReviewerReputation recalculation cancelled reviewer={ReviewerId}",
                reviewerId);
            throw;
        }
        catch (Exception exception)
        {
            FeedbackTelemetry.RecordReviewerRecalculation(
                ReviewerRecalculationOperation.Single,
                ReviewerRecalculationOutcome.Failed,
                processedCount: 0,
                repairedCount: 0,
                Stopwatch.GetElapsedTime(startedAt));
            _logger.LogError(
                exception,
                "ReviewerReputation recalculation failed reviewer={ReviewerId}",
                reviewerId);
            throw;
        }
    }

    public async Task<int> RecalculateAllAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var processedCount = 0;
        var repairedCount = 0;

        try
        {
            var reviewerIds = await _db.Database
                .SqlQueryRaw<long>(CandidateReviewerSql)
                .ToListAsync(cancellationToken);

            foreach (var reviewerId in reviewerIds)
            {
                var result = await RecalculateCoreAsync(
                    reviewerId,
                    cancellationToken);
                if (result.Snapshot is not null)
                {
                    processedCount++;
                    if (result.Repaired)
                    {
                        repairedCount++;
                    }
                }
            }

            FeedbackTelemetry.RecordReviewerRecalculation(
                ReviewerRecalculationOperation.Full,
                ReviewerRecalculationOutcome.Completed,
                processedCount,
                repairedCount,
                Stopwatch.GetElapsedTime(startedAt));
            _logger.LogInformation(
                "ReviewerReputation full recalculation processed={ProcessedCount} repaired={RepairedCount}",
                processedCount,
                repairedCount);
            return processedCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FeedbackTelemetry.RecordReviewerRecalculation(
                ReviewerRecalculationOperation.Full,
                ReviewerRecalculationOutcome.Cancelled,
                processedCount,
                repairedCount,
                Stopwatch.GetElapsedTime(startedAt));
            _logger.LogWarning(
                "ReviewerReputation full recalculation cancelled processed={ProcessedCount} repaired={RepairedCount}",
                processedCount,
                repairedCount);
            throw;
        }
        catch (Exception exception)
        {
            FeedbackTelemetry.RecordReviewerRecalculation(
                ReviewerRecalculationOperation.Full,
                ReviewerRecalculationOutcome.Failed,
                processedCount,
                repairedCount,
                Stopwatch.GetElapsedTime(startedAt));
            _logger.LogError(
                exception,
                "ReviewerReputation full recalculation failed processed={ProcessedCount} repaired={RepairedCount}",
                processedCount,
                repairedCount);
            throw;
        }
    }

    private async Task<RecalculationResult> RecalculateCoreAsync(
        long reviewerId,
        CancellationToken cancellationToken)
    {
        var before = await GetAsync(reviewerId, cancellationToken);
        await _db.Database.ExecuteSqlRawAsync(
            RefreshSql,
            [new NpgsqlParameter("reviewerId", reviewerId)],
            cancellationToken);
        var after = await GetAsync(reviewerId, cancellationToken);
        var repaired = before is null
            ? after is not null
            : after is null || before.Revision != after.Revision;
        return new RecalculationResult(after, repaired);
    }

    private static ReviewerReputationSnapshot CreateSnapshot(
        ReviewerReputation reputation)
    {
        var scores = ReviewerReputationPolicy.Calculate(
            reputation.CompletedReviewCount,
            reputation.ExpiredAssignmentCount,
            reputation.RatingCount,
            reputation.RatingScoreSum);
        return new ReviewerReputationSnapshot(
            reputation.ReviewerId,
            reputation.CompletedReviewCount,
            reputation.ExpiredAssignmentCount,
            reputation.RatingCount,
            reputation.RatingScoreSum,
            reputation.Revision,
            reputation.UpdatedAt,
            scores.QualityScore,
            scores.CompletionReliabilityScore,
            scores.TrustScore,
            scores.Grade);
    }

    private sealed record RecalculationResult(
        ReviewerReputationSnapshot? Snapshot,
        bool Repaired);
}
