using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed record AuthenticationEmailOutboxProcessResult(
    int SentCount,
    int ExpiredCount);

public sealed class AuthenticationEmailOutboxProcessor
{
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

        for (var index = 0; index < batchSize; index++)
        {
            var outcome = await ProcessNextAsync(cancellationToken);
            if (outcome == ProcessNextOutcome.None)
            {
                break;
            }

            if (outcome == ProcessNextOutcome.Sent)
            {
                sentCount++;
            }
            else
            {
                expiredCount++;
            }
        }

        return new AuthenticationEmailOutboxProcessResult(sentCount, expiredCount);
    }

    private async Task<ProcessNextOutcome> ProcessNextAsync(
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);

        // A row lock is held through SMTP delivery. SKIP LOCKED lets another app
        // instance work on a different row without sending this message twice.
        var candidates = await _db.AuthenticationEmailOutboxMessages
            .FromSqlRaw(
                """
                SELECT *
                  FROM authentication_email_outbox_messages
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

        if (message.ExpiresAt <= _timeProvider.GetUtcNow())
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

        if (message.Kind != AuthenticationEmailMessageKind.EmailConfirmation)
        {
            throw new InvalidOperationException(
                "The authentication email message kind is not supported.");
        }

        await _sender.SendEmailConfirmationAsync(
            new AuthenticationEmailDelivery(
                recipientAddress,
                plaintextToken,
                message.ExpiresAt),
            cancellationToken);

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
    }
}
