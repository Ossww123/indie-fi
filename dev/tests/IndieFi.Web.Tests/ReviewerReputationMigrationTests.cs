using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewerReputationMigrationTests
{
    private const string PreviousMigration =
        "20260817082406_AddFeedbackQualityStorage";

    private readonly PostgreSqlFixture _fixture;

    public ReviewerReputationMigrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExistingReviewHistory_IsBackfilledWhenProjectionMigrationRuns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = $"reviewer_reputation_{Guid.NewGuid():N}";
        var databaseCreated = false;

        try
        {
            await ExecuteAdminCommandAsync(
                $"CREATE DATABASE \"{database}\";",
                cancellationToken);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_fixture.CreateOwnerConnectionString(database))
                .Options;
            await using (var db = new AppDbContext(options))
            {
                var migrator = db.Database.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration, cancellationToken);
                var reviewerId = await SeedExistingHistoryAsync(db, cancellationToken);

                await migrator.MigrateAsync(cancellationToken: cancellationToken);
                Assert.Contains(
                    "20260817112145_AddReviewerReputationProjection",
                    await db.Database.GetAppliedMigrationsAsync(cancellationToken));

                db.ChangeTracker.Clear();
                var reputation = await db.ReviewerReputations
                    .AsNoTracking()
                    .SingleAsync(
                        item => item.ReviewerId == reviewerId,
                        cancellationToken);
                Assert.Equal(1, reputation.CompletedReviewCount);
                Assert.Equal(1, reputation.ExpiredAssignmentCount);
                Assert.Equal(1, reputation.RatingCount);
                Assert.Equal(5, reputation.RatingScoreSum);
                Assert.True(reputation.Revision > 1);
            }
        }
        finally
        {
            if (databaseCreated)
            {
                await ExecuteAdminCommandAsync(
                    $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);",
                    CancellationToken.None);
            }
        }
    }

    private static async Task<long> SeedExistingHistoryAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var reviewer = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        var completedOwner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
        var completedGame = await ReviewTestData.CreateGameAsync(db, completedOwner.Id);
        var completedRequest = await ReviewTestData.CreateQueuedRequestAsync(
            db,
            completedOwner.Id,
            completedGame.Id);
        var completedAt = DateTimeOffset.UtcNow;
        completedRequest.Status = ReviewRequestStatus.Completed;
        completedRequest.CompletedAt = completedAt;
        var completedAssignment = new ReviewAssignment
        {
            RequestId = completedRequest.Id,
            GameId = completedGame.Id,
            ReviewerId = reviewer.Id,
            ClaimOperationId = Guid.NewGuid(),
            Status = ReviewAssignmentStatus.Completed,
            LeasedUntil = completedAt.AddHours(1),
            CompletedAt = completedAt,
        };
        db.ReviewAssignments.Add(completedAssignment);
        await db.SaveChangesAsync(cancellationToken);

        var earnEntry = new CreditLedgerEntry
        {
            UserId = reviewer.Id,
            Kind = CreditEntryKind.Earn,
            Delta = 1,
        };
        var submission = new FeedbackSubmission
        {
            AssignmentId = completedAssignment.Id,
            ReviewerId = reviewer.Id,
            EarnEntry = earnEntry,
            Body = "마이그레이션 백필을 확인하기 위한 충분히 구체적이고 긴 피드백 본문입니다.",
        };
        db.CreditLedgerEntries.Add(earnEntry);
        db.FeedbackSubmissions.Add(submission);
        await db.SaveChangesAsync(cancellationToken);
        db.FeedbackRatings.Add(new FeedbackRating
        {
            FeedbackSubmissionId = submission.Id,
            RaterId = completedOwner.Id,
            ReviewerId = reviewer.Id,
            Score = 5,
        });

        var expiredOwner = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
        var expiredGame = await ReviewTestData.CreateGameAsync(db, expiredOwner.Id);
        var expiredRequest = await ReviewTestData.CreateQueuedRequestAsync(
            db,
            expiredOwner.Id,
            expiredGame.Id);
        var expiredAt = DateTimeOffset.UtcNow;
        db.ReviewAssignments.Add(new ReviewAssignment
        {
            RequestId = expiredRequest.Id,
            GameId = expiredGame.Id,
            ReviewerId = reviewer.Id,
            ClaimOperationId = Guid.NewGuid(),
            Status = ReviewAssignmentStatus.Expired,
            LeasedUntil = expiredAt.AddMinutes(-1),
            ExpiredAt = expiredAt,
        });
        db.ReviewerReputations.Add(new ReviewerReputation
        {
            ReviewerId = reviewer.Id,
        });
        await db.SaveChangesAsync(cancellationToken);
        return reviewer.Id;
    }

    private async Task ExecuteAdminCommandAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(
            _fixture.CreateOwnerConnectionString("postgres"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
