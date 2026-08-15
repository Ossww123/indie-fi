using IndieFi.Web.Modules.Credits.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FeedbackCreditOriginConstraintTests
{
    private readonly PostgreSqlFixture _fixture;

    public FeedbackCreditOriginConstraintTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LedgerEntry_WhenKindIsUnknown_IsRejected()
    {
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0)).Id;
        }

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO credit_ledger_entries (user_id, kind, delta)
                VALUES ({0}, 'Unknown', 1)
                """,
                [userId],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_credit_ledger_entries_kind", exception.ConstraintName);
    }

    [Fact]
    public async Task LedgerEntry_WhenKindAndDeltaDoNotMatch_IsRejected()
    {
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 5)).Id;
        }

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO credit_ledger_entries (user_id, kind, delta)
                VALUES ({0}, 'Spend', -2)
                """,
                [userId],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_credit_ledger_entries_kind_delta", exception.ConstraintName);
    }

    [Fact]
    public async Task FeedbackSubmission_WhenEarnEntryBelongsToAnotherUser_IsRejected()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        long otherEarnEntryId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var otherReviewer = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 0);
            var otherEarnEntry = new CreditLedgerEntry
            {
                UserId = otherReviewer.Id,
                Kind = CreditEntryKind.Earn,
                Delta = 1,
            };
            arrangeDb.CreditLedgerEntries.Add(otherEarnEntry);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            otherEarnEntryId = otherEarnEntry.Id;
        }

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO feedback_submissions (
                    assignment_id, reviewer_id, earn_entry_id, body)
                VALUES ({0}, {1}, {2}, {3})
                """,
                new object[]
                {
                    assignment.AssignmentId,
                    assignment.ReviewerId,
                    otherEarnEntryId,
                    "This feedback must not use another reviewer's credit entry.",
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task ReviewRequest_WhenSpendEntryBelongsToAnotherUser_IsRejected()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        long otherSpendEntryId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var otherUser = await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, 1);
            var otherSpendEntry = new CreditLedgerEntry
            {
                UserId = otherUser.Id,
                Kind = CreditEntryKind.Spend,
                Delta = -1,
            };
            arrangeDb.CreditLedgerEntries.Add(otherSpendEntry);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            otherSpendEntryId = otherSpendEntry.Id;
        }

        await using var db = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE review_requests SET spend_entry_id = {0} WHERE id = {1}",
                [otherSpendEntryId, assignment.RequestId],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }
}
