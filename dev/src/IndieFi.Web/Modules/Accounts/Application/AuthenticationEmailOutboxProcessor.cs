using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed record AuthenticationEmailOutboxProcessResult(
    int SentCount,
    int ExpiredCount,
    int FailedCount);

public sealed class AuthenticationEmailOutboxProcessor
{
    /// <summary>
    /// How long a failed message waits before it is eligible again. A failure must not
    /// block the messages behind it, so the row is rescheduled instead of retried at once.
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly AppDbContext _db;
    private readonly IAuthenticationEmailSender _sender;
    private readonly EmailProtectionService _emailProtection;
    private readonly AuthenticationEmailMessageProtectionService _messageProtection;
    private readonly TimeProvider _timeProvider;

    public AuthenticationEmailOutboxProcessor(
        AppDbContext db,
        IAuthenticationEmailSender sender,
        EmailProtectionService emailProtection,
        AuthenticationEmailMessageProtectionService messageProtection,
        TimeProvider timeProvider)
    {
        _db = db;
        _sender = sender;
        _emailProtection = emailProtection;
        _messageProtection = messageProtection;
        _timeProvider = timeProvider;
    }

    public async Task<AuthenticationEmailOutboxProcessResult> ProcessBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var sentCount = 0;
        var expiredCount = 0;
        var failedCount = 0;

        for (var index = 0; index < batchSize; index++)
        {
            var outcome = await ProcessNextAsync(cancellationToken);
            if (outcome == ProcessNextOutcome.None)
            {
                break;
            }

            switch (outcome)
            {
                case ProcessNextOutcome.Sent:
                    sentCount++;
                    break;
                case ProcessNextOutcome.Expired:
                    expiredCount++;
                    break;
                default:
                    failedCount++;
                    break;
            }
        }

        return new AuthenticationEmailOutboxProcessResult(
            sentCount,
            expiredCount,
            failedCount);
    }

    private async Task<ProcessNextOutcome> ProcessNextAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        var now = _timeProvider.GetUtcNow();

        // A row lock is held through SMTP delivery. SKIP LOCKED lets another app
        // instance work on a different row without sending this message twice.
        var candidates = await _db.AuthenticationEmailOutboxMessages
            .FromSqlInterpolated(
                $"""
                SELECT *
                  FROM authentication_email_outbox_messages
                 WHERE next_attempt_at <= {now}
                 ORDER BY id
                 LIMIT 1
                   FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        var message = candidates.SingleOrDefault();

        if (message is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ProcessNextOutcome.None;
        }

        if (message.ExpiresAt <= now)
        {
            _db.AuthenticationEmailOutboxMessages.Remove(message);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProcessNextOutcome.Expired;
        }

        var user = await _db.Users
            .AsNoTracking()
            .SingleAsync(account => account.Id == message.UserId, cancellationToken);
        var recipientAddress = _emailProtection.Unprotect(new ProtectedEmailAddress(
            user.EmailCiphertext,
            user.EmailNonce,
            user.EmailAuthenticationTag,
            user.EmailEncryptionKeyId));
        var plaintextToken = _messageProtection.Unprotect(
            message.UserId,
            message.Kind,
            new ProtectedAuthenticationEmailPayload(
                message.PayloadCiphertext,
                message.PayloadNonce,
                message.PayloadAuthenticationTag,
                message.PayloadKeyId));

        if (message.Kind is not AuthenticationEmailMessageKind.EmailConfirmation
            and not AuthenticationEmailMessageKind.PasswordReset)
        {
            throw new InvalidOperationException(
                "The authentication email message kind is not supported.");
        }

        try
        {
            var delivery = new AuthenticationEmailDelivery(
                recipientAddress,
                plaintextToken,
                message.ExpiresAt);
            switch (message.Kind)
            {
                case AuthenticationEmailMessageKind.EmailConfirmation:
                    await _sender.SendEmailConfirmationAsync(
                        delivery,
                        cancellationToken);
                    break;
                case AuthenticationEmailMessageKind.PasswordReset:
                    await _sender.SendPasswordResetAsync(
                        delivery,
                        cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException(
                        "The authentication email message kind is not supported.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // Committing the new attempt time is the point: rolling back here would put
            // the message straight back at the front of the queue. An SMTP timeout also
            // surfaces as a cancellation, so only a real shutdown is allowed to propagate.
            message.NextAttemptAt = now.Add(RetryDelay);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProcessNextOutcome.Failed;
        }

        _db.AuthenticationEmailOutboxMessages.Remove(message);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProcessNextOutcome.Sent;
    }

    private enum ProcessNextOutcome
    {
        None,
        Sent,
        Expired,
        Failed,
    }
}
