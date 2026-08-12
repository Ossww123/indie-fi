using System.Diagnostics;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewLeaseSweepWorkerTests
{
    private static readonly TimeSpan WorkerInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

    private readonly PostgreSqlFixture _fixture;

    public ReviewLeaseSweepWorkerTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExecuteAsync_WhenALeaseHasExpired_SweepsWithoutAnyonePressingTheButton()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await ExpireLeaseAsync(assignment.AssignmentId);

        using var provider = BuildServiceProvider();
        var worker = new ReviewLeaseSweepWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<ReviewLeaseSweepWorker>>(),
            WorkerInterval);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilExpiredAsync(assignment.AssignmentId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await AssertExpiredAndQueuedAsync(assignment);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOneSweepFails_KeepsRunning()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await ExpireLeaseAsync(assignment.AssignmentId);

        using var provider = BuildServiceProvider();
        var scopeFactory = new FailsOnceScopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>());
        var worker = new ReviewLeaseSweepWorker(
            scopeFactory,
            provider.GetRequiredService<ILogger<ReviewLeaseSweepWorker>>(),
            WorkerInterval);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilExpiredAsync(assignment.AssignmentId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await AssertExpiredAndQueuedAsync(assignment);
        Assert.True(scopeFactory.CreateScopeCallCount >= 2);
    }

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddScoped<ReviewLeaseSweepService>();
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private async Task ExpireLeaseAsync(long assignmentId)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_assignments
               SET leased_until = now() - INTERVAL '1 minute'
             WHERE id = {0}
            """,
            [assignmentId],
            TestContext.Current.CancellationToken);
    }

    private async Task WaitUntilExpiredAsync(long assignmentId)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < PollTimeout)
        {
            await using var db = _fixture.NewDbContext();
            var status = await db.ReviewAssignments
                .Where(item => item.Id == assignmentId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken);

            if (status == ReviewAssignmentStatus.Expired)
            {
                return;
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The worker did not expire the assignment within ten seconds.");
    }

    private async Task AssertExpiredAndQueuedAsync(ReviewTestData.LiveAssignment assignment)
    {
        await using var db = _fixture.NewDbContext();
        var assignmentStatus = await db.ReviewAssignments
            .Where(item => item.Id == assignment.AssignmentId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);
        var requestStatus = await db.ReviewRequests
            .Where(item => item.Id == assignment.RequestId)
            .Select(item => item.Status)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ReviewAssignmentStatus.Expired, assignmentStatus);
        Assert.Equal(ReviewRequestStatus.Queued, requestStatus);
    }

    private sealed class FailsOnceScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        private int _createScopeCallCount;

        public FailsOnceScopeFactory(IServiceScopeFactory inner)
        {
            _inner = inner;
        }

        public int CreateScopeCallCount => Volatile.Read(ref _createScopeCallCount);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _createScopeCallCount) == 1)
            {
                throw new InvalidOperationException("Expected first scope creation failure.");
            }

            return _inner.CreateScope();
        }
    }
}
