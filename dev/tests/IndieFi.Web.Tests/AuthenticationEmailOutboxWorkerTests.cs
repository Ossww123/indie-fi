using System.Diagnostics;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationEmailOutboxWorkerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 7, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AuthenticationEmailOutboxWorkerTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExecuteAsync_WhenFirstDeliveryFails_RetriesOnTheNextInterval()
    {
        await StageMessageAsync();
        var sender = new FailsOnceSender();
        var logger = new RecordingLogger<AuthenticationEmailOutboxWorker>();

        using var provider = BuildServiceProvider(sender);
        var worker = new AuthenticationEmailOutboxWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            enabled: true,
            interval: TimeSpan.FromMilliseconds(100),
            batchSize: 10);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilOutboxIsEmptyAsync();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(sender.CallCount >= 2);
        Assert.DoesNotContain(
            "private-recipient@example.test",
            string.Join(Environment.NewLine, logger.Messages),
            StringComparison.Ordinal);
        Assert.All(logger.Exceptions, exception => Assert.Null(exception));
    }

    private ServiceProvider BuildServiceProvider(IAuthenticationEmailSender sender)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(_fixture.ConnectionString));
        services.AddScoped<AuthenticationEmailOutboxProcessor>();
        services.AddSingleton<IAuthenticationEmailSender>(sender);
        services.AddSingleton(TestAccountProtection.CreateService());
        services.AddSingleton(TestAccountProtection.CreateMessageProtectionService());
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private async Task StageMessageAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.AuthenticationEmailOutboxMessages.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);

        var user = TestAccountProtection.AddUser(
            db,
            $"worker-{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var token = WebEncoders.Base64UrlEncode(
            Enumerable.Repeat((byte)41, EmailConfirmationTokenService.TokenEntropySize)
                .ToArray());
        new AuthenticationEmailOutboxService(
                db,
                TestAccountProtection.CreateMessageProtectionService(),
                new FixedTimeProvider(Now))
            .StageEmailConfirmation(user.Id, token, Now.AddHours(1));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task WaitUntilOutboxIsEmptyAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await using var db = _fixture.NewDbContext();
            if (!await db.AuthenticationEmailOutboxMessages.AnyAsync(
                    TestContext.Current.CancellationToken))
            {
                return;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                TestContext.Current.CancellationToken);
        }

        Assert.Fail("The worker did not retry and deliver the message within ten seconds.");
    }

    private sealed class FailsOnceSender : IAuthenticationEmailSender
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task SendEmailConfirmationAsync(
            AuthenticationEmailDelivery delivery,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                throw new InvalidOperationException(
                    "private-recipient@example.test was rejected.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];

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
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
