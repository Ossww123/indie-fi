using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class KeyDropEligibilityService
{
    private readonly AppDbContext _db;

    public KeyDropEligibilityService(AppDbContext db)
    {
        _db = db;
    }

    public KeyDropEligibilityOutcome Evaluate(KeyDropEligibilitySnapshot snapshot) =>
        KeyDropEligibilityPolicy.Evaluate(snapshot);

    public async Task<KeyDropEligibilityOutcome> EvaluateAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Database
            .SqlQuery<EligibilityRow>($$"""
                WITH drop_state AS MATERIALIZED (
                    SELECT key_drop.minimum_account_age_days AS "MinimumAccountAgeDays",
                           key_drop.minimum_completed_review_count AS "MinimumCompletedReviewCount"
                    FROM key_drops AS key_drop
                    WHERE key_drop.id = {{dropId}}
                ),
                account_state AS MATERIALIZED (
                    SELECT app_user.created_at AS "AccountCreatedAt"
                    FROM users AS app_user
                    WHERE app_user.id = {{userId}}
                      AND app_user.account_status = 'Active'
                      AND app_user.suspended_until IS NULL
                )
                SELECT EXISTS (SELECT 1 FROM drop_state) AS "DropExists",
                       EXISTS (SELECT 1 FROM account_state) AS "AccountAvailable",
                       drop_state."MinimumAccountAgeDays",
                       drop_state."MinimumCompletedReviewCount",
                       account_state."AccountCreatedAt",
                       (
                           SELECT COUNT(*)::integer
                           FROM review_assignments AS assignment
                           WHERE assignment.reviewer_id = {{userId}}
                             AND assignment.status = 'Completed'
                       ) AS "CompletedReviewCount",
                       now() AS "DatabaseNow"
                FROM (SELECT 1) AS anchor
                LEFT JOIN drop_state ON true
                LEFT JOIN account_state ON true
                """)
            .ToListAsync(cancellationToken);
        var row = rows.Single();

        return Evaluate(new KeyDropEligibilitySnapshot(
            row.DropExists,
            row.AccountAvailable,
            row.MinimumAccountAgeDays,
            row.MinimumCompletedReviewCount,
            row.AccountCreatedAt,
            row.CompletedReviewCount,
            row.DatabaseNow));
    }

    private sealed record EligibilityRow
    {
        public bool DropExists { get; init; }

        public bool AccountAvailable { get; init; }

        public int? MinimumAccountAgeDays { get; init; }

        public int? MinimumCompletedReviewCount { get; init; }

        public DateTimeOffset? AccountCreatedAt { get; init; }

        public int CompletedReviewCount { get; init; }

        public DateTimeOffset DatabaseNow { get; init; }
    }
}
