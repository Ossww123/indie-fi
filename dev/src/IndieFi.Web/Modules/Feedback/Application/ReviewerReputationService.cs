using IndieFi.Web.Data;
using IndieFi.Web.Modules.Feedback.Domain;
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

        await _db.Database.ExecuteSqlRawAsync(
            RefreshSql,
            [new NpgsqlParameter("reviewerId", reviewerId)],
            cancellationToken);
        var snapshot = await GetAsync(reviewerId, cancellationToken);

        _logger.LogInformation(
            "ReviewerReputation recalculated reviewer={ReviewerId} revision={Revision}",
            reviewerId,
            snapshot?.Revision);
        return snapshot;
    }

    public async Task<int> RecalculateAllAsync(
        CancellationToken cancellationToken = default)
    {
        var reviewerIds = await _db.Database
            .SqlQueryRaw<long>(CandidateReviewerSql)
            .ToListAsync(cancellationToken);

        foreach (var reviewerId in reviewerIds)
        {
            await RecalculateAsync(reviewerId, cancellationToken);
        }

        _logger.LogInformation(
            "ReviewerReputation full recalculation processed={ReviewerCount}",
            reviewerIds.Count);
        return reviewerIds.Count;
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
}
