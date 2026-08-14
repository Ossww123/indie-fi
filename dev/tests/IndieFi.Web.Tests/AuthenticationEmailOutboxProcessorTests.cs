using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationEmailOutboxProcessorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 6, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AuthenticationEmailOutboxProcessorTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenMessageIsPending_SendsDecryptedValuesAndDeletesIt()
    {
        await ResetOutboxAsync();
        var pending = await StageMessageAsync(Now, Now.AddHours(1));
        var sender = new RecordingSender();

        await using var db = _fixture.NewDbContext();
        var result = await NewProcessor(db, sender).ProcessBatchAsync(
            batchSize: 10,
            TestContext.Current.CancellationToken);

        var delivery = Assert.Single(sender.Deliveries);
        Assert.Equal(1, result.SentCount);
        Assert.Equal(0, result.ExpiredCount);
        Assert.Equal(pending.Email, delivery.RecipientAddress);
        Assert.Equal(pending.Token, delivery.PlaintextToken);
        Assert.Equal(pending.ExpiresAt, delivery.ExpiresAt);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenMessageHasExpired_DeletesWithoutSending()
    {
        await ResetOutboxAsync();
        await StageMessageAsync(Now.AddHours(-2), Now.AddHours(-1));
        var sender = new RecordingSender();

        await using var db = _fixture.NewDbContext();
        var result = await NewProcessor(db, sender).ProcessBatchAsync(
            batchSize: 10,
            TestContext.Current.CancellationToken);

        Assert.Empty(sender.Deliveries);
        Assert.Equal(0, result.SentCount);
        Assert.Equal(1, result.ExpiredCount);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenDeliveryFails_RetriesOnlyAfterTheRetryDelay()
    {
        await ResetOutboxAsync();
        await StageMessageAsync(Now, Now.AddHours(1));

        await using (var db = _fixture.NewDbContext())
        {
            var result = await NewProcessor(db, new AlwaysFailsSender()).ProcessBatchAsync(
                batchSize: 10,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, result.SentCount);
            Assert.Equal(1, result.FailedCount);
        }

        var tooEarly = new RecordingSender();
        await using (var db = _fixture.NewDbContext())
        {
            var processor = NewProcessor(
                db,
                tooEarly,
                Now.Add(AuthenticationEmailOutboxProcessor.RetryDelay).AddSeconds(-1));
            var result = await processor.ProcessBatchAsync(
                batchSize: 10,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, result.SentCount);
            Assert.Empty(tooEarly.Deliveries);
        }

        var afterDelay = new RecordingSender();
        await using (var db = _fixture.NewDbContext())
        {
            var processor = NewProcessor(
                db,
                afterDelay,
                Now.Add(AuthenticationEmailOutboxProcessor.RetryDelay));
            var result = await processor.ProcessBatchAsync(
                batchSize: 10,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, result.SentCount);
            Assert.Single(afterDelay.Deliveries);
        }
    }

    /// <summary>
    /// A permanently undeliverable address must not hold the queue. Without this the
    /// oldest failing row is picked again on every pass and no one else receives mail
    /// until it expires.
    /// </summary>
    [Fact]
    public async Task ProcessBatchAsync_WhenOneMessageAlwaysFails_StillSendsTheNextMessage()
    {
        await ResetOutboxAsync();
        var blocked = await StageMessageAsync(Now, Now.AddHours(1));
        var following = await StageMessageAsync(Now, Now.AddHours(1));
        var sender = new FailsForRecipientSender(blocked.Email);

        await using var db = _fixture.NewDbContext();
        var result = await NewProcessor(db, sender).ProcessBatchAsync(
            batchSize: 10,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SentCount);
        Assert.Equal(1, result.FailedCount);

        var delivery = Assert.Single(sender.Deliveries);
        Assert.Equal(following.Email, delivery.RecipientAddress);

        await using var verificationDb = _fixture.NewDbContext();
        var remaining = Assert.Single(
            await verificationDb.AuthenticationEmailOutboxMessages.ToListAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(
            Now.Add(AuthenticationEmailOutboxProcessor.RetryDelay),
            remaining.NextAttemptAt);

        await verificationDb.AuthenticationEmailOutboxMessages.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenAnotherProcessorOwnsTheRow_DoesNotSendItTwice()
    {
        await ResetOutboxAsync();
        await StageMessageAsync(Now, Now.AddHours(1));
        var sender = new BlockingSender();

        await using var firstDb = _fixture.NewDbContext();
        await using var secondDb = _fixture.NewDbContext();
        var firstTask = NewProcessor(firstDb, sender).ProcessBatchAsync(
            batchSize: 1,
            TestContext.Current.CancellationToken);

        await sender.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        var secondResult = await NewProcessor(secondDb, sender).ProcessBatchAsync(
            batchSize: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, secondResult.SentCount);
        Assert.Equal(0, secondResult.ExpiredCount);

        sender.Release.TrySetResult();
        var firstResult = await firstTask;

        Assert.Equal(1, firstResult.SentCount);
        Assert.Equal(1, sender.CallCount);
    }

    private AuthenticationEmailOutboxProcessor NewProcessor(
        AppDbContext db,
        IAuthenticationEmailSender sender,
        DateTimeOffset? now = null)
    {
        return new AuthenticationEmailOutboxProcessor(
            db,
            sender,
            TestAccountProtection.CreateService(),
            TestAccountProtection.CreateMessageProtectionService(),
            new FixedTimeProvider(now ?? Now));
    }

    private async Task<PendingMessage> StageMessageAsync(
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        await using var db = _fixture.NewDbContext();
        var email = $"outbox-{Guid.NewGuid():N}@example.test";
        var user = TestAccountProtection.AddUser(db, email);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var token = WebEncoders.Base64UrlEncode(
            Enumerable.Repeat((byte)37, EmailConfirmationTokenService.TokenEntropySize)
                .ToArray());
        var outbox = new AuthenticationEmailOutboxService(
            db,
            TestAccountProtection.CreateMessageProtectionService(),
            new FixedTimeProvider(createdAt));
        outbox.StageEmailConfirmation(user.Id, token, expiresAt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new PendingMessage(email, token, expiresAt);
    }

    private async Task ResetOutboxAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.AuthenticationEmailOutboxMessages.ExecuteDeleteAsync(
            TestContext.Current.CancellationToken);
    }

    private sealed record PendingMessage(
        string Email,
        string Token,
        DateTimeOffset ExpiresAt);

    private sealed class RecordingSender : IAuthenticationEmailSender
    {
        public List<AuthenticationEmailDelivery> Deliveries { get; } = [];

        public Task SendEmailConfirmationAsync(
            AuthenticationEmailDelivery delivery,
            CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysFailsSender : IAuthenticationEmailSender
    {
        public Task SendEmailConfirmationAsync(
            AuthenticationEmailDelivery delivery,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Expected delivery failure.");
        }
    }

    private sealed class FailsForRecipientSender : IAuthenticationEmailSender
    {
        private readonly string _failingRecipient;

        public FailsForRecipientSender(string failingRecipient)
        {
            _failingRecipient = failingRecipient;
        }

        public List<AuthenticationEmailDelivery> Deliveries { get; } = [];

        public Task SendEmailConfirmationAsync(
            AuthenticationEmailDelivery delivery,
            CancellationToken cancellationToken)
        {
            if (delivery.RecipientAddress == _failingRecipient)
            {
                throw new InvalidOperationException("Expected delivery failure.");
            }

            Deliveries.Add(delivery);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingSender : IAuthenticationEmailSender
    {
        private int _callCount;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task SendEmailConfirmationAsync(
            AuthenticationEmailDelivery delivery,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
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
}
