using IndieFi.Web.Modules.Feedback.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewStateConstraintTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReviewStateConstraintTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReviewRequest_WhenTerminalTimestampIsMissing_IsRejected()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using var db = _fixture.NewDbContext();

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE review_requests SET status = 'Completed' WHERE id = {0}",
                [assignment.RequestId],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_review_requests_terminal_timestamps", exception.ConstraintName);
    }

    [Fact]
    public async Task ReviewAssignment_WhenTerminalTimestampIsMissing_IsRejected()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using var db = _fixture.NewDbContext();

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE review_assignments SET status = 'Expired' WHERE id = {0}",
                [assignment.AssignmentId],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_review_assignments_terminal_timestamps", exception.ConstraintName);
    }

    [Fact]
    public async Task ReviewStates_WhenUnknown_AreRejected()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await using var requestDb = _fixture.NewDbContext();

        var requestException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await requestDb.Database.ExecuteSqlRawAsync(
                "UPDATE review_requests SET status = 'Unknown' WHERE id = {0}",
                [assignment.RequestId],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, requestException.SqlState);
        Assert.Equal("CK_review_requests_status", requestException.ConstraintName);

        await using var assignmentDb = _fixture.NewDbContext();
        var assignmentException = await Assert.ThrowsAsync<PostgresException>(async () =>
            await assignmentDb.Database.ExecuteSqlRawAsync(
                "UPDATE review_assignments SET status = 'Unknown' WHERE id = {0}",
                [assignment.AssignmentId],
                TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.CheckViolation, assignmentException.SqlState);
        Assert.Equal("CK_review_assignments_status", assignmentException.ConstraintName);
    }
}
