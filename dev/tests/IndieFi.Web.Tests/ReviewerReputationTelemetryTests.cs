using System.Diagnostics.Metrics;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Feedback.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReviewerReputationTelemetryTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReviewerReputationTelemetryTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RecalculateAsync_RecordsCompletedRepairWithoutIdentifierTags()
    {
        long reviewerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            reviewerId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        var measurements = new List<Measurement>();
        using var listener = Listen(measurements);
        await using var db = _fixture.NewDbContext();
        var service = new ReviewerReputationService(
            db,
            new RecordingLogger<ReviewerReputationService>());

        var result = await service.RecalculateAsync(
            reviewerId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(measurements, item =>
            item.Name == "indiefi.feedback.reputation.recalculations"
            && item.Value == 1
            && item.Tag("operation") == "single"
            && item.Tag("outcome") == "completed");
        Assert.Contains(measurements, item =>
            item.Name == "indiefi.feedback.reputation.reviewers"
            && item.Value == 1
            && item.Tag("operation") == "single"
            && item.Tag("result") == "repaired");
        Assert.Contains(measurements, item =>
            item.Name == "indiefi.feedback.reputation.duration"
            && item.Value >= 0
            && item.Tag("operation") == "single"
            && item.Tag("outcome") == "completed");
        Assert.All(measurements.SelectMany(item => item.Tags), tag => Assert.Contains(
            tag.Key,
            new[] { "operation", "outcome", "result" }));
        Assert.DoesNotContain(measurements.SelectMany(item => item.Tags), tag =>
            tag.Key.Contains("reviewer", StringComparison.OrdinalIgnoreCase)
            || tag.Key.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecalculateAsync_WhenDatabaseFails_RecordsFailureAndLogsException()
    {
        var measurements = new List<Measurement>();
        using var listener = Listen(measurements);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=unreachable;Username=none;"
                + "Password=none;Timeout=1;Command Timeout=1")
            .Options;
        await using var db = new AppDbContext(options);
        var logger = new RecordingLogger<ReviewerReputationService>();
        var service = new ReviewerReputationService(db, logger);

        await Assert.ThrowsAnyAsync<Exception>(() => service.RecalculateAsync(
            1,
            TestContext.Current.CancellationToken));

        Assert.Contains(measurements, item =>
            item.Name == "indiefi.feedback.reputation.recalculations"
            && item.Value == 1
            && item.Tag("operation") == "single"
            && item.Tag("outcome") == "failed");
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Exception is not null
            && entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Telemetry_RecordsBoundedFullRecalculationResults()
    {
        var measurements = new List<Measurement>();
        using var listener = Listen(measurements);

        FeedbackTelemetry.RecordReviewerRecalculation(
            ReviewerRecalculationOperation.Full,
            ReviewerRecalculationOutcome.Completed,
            processedCount: 3,
            repairedCount: 1,
            TimeSpan.FromMilliseconds(25));

        Assert.Contains(measurements, item =>
            item.Name == "indiefi.feedback.reputation.reviewers"
            && item.Value == 1
            && item.Tag("operation") == "full"
            && item.Tag("result") == "repaired");
        Assert.Contains(measurements, item =>
            item.Name == "indiefi.feedback.reputation.reviewers"
            && item.Value == 2
            && item.Tag("operation") == "full"
            && item.Tag("result") == "unchanged");
    }

    private static MeterListener Listen(ICollection<Measurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == FeedbackTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    private sealed record Measurement(
        string Name,
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public string? Tag(string name) => Tags
            .SingleOrDefault(tag => tag.Key == name)
            .Value?
            .ToString();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(
                logLevel,
                formatter(state, exception),
                exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);
}
