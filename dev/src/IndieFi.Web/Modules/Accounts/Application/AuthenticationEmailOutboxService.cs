using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed class AuthenticationEmailOutboxService
{
    private readonly AppDbContext _db;
    private readonly AuthenticationEmailMessageProtectionService _protection;
    private readonly TimeProvider _timeProvider;

    public AuthenticationEmailOutboxService(
        AppDbContext db,
        AuthenticationEmailMessageProtectionService protection,
        TimeProvider timeProvider)
    {
        _db = db;
        _protection = protection;
        _timeProvider = timeProvider;
    }

    public AuthenticationEmailOutboxMessage StageEmailConfirmation(
        long userId,
        string plaintextToken,
        DateTimeOffset expiresAt)
    {
        ValidateToken(
            plaintextToken,
            EmailConfirmationTokenService.EncodedTokenLength,
            EmailConfirmationTokenService.TokenEntropySize,
            "email confirmation");
        return Stage(
            userId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            plaintextToken,
            expiresAt);
    }

    public AuthenticationEmailOutboxMessage StagePasswordReset(
        long userId,
        string plaintextToken,
        DateTimeOffset expiresAt)
    {
        ValidateToken(
            plaintextToken,
            PasswordResetTokenService.EncodedTokenLength,
            PasswordResetTokenService.TokenEntropySize,
            "password reset");
        return Stage(
            userId,
            AuthenticationEmailMessageKind.PasswordReset,
            plaintextToken,
            expiresAt);
    }

    public async Task<AuthenticationEmailOutboxMessage> ReplaceEmailConfirmationAsync(
        long userId,
        string plaintextToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await DeletePendingAsync(
            userId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            cancellationToken);
        return StageEmailConfirmation(userId, plaintextToken, expiresAt);
    }

    public async Task<AuthenticationEmailOutboxMessage> ReplacePasswordResetAsync(
        long userId,
        string plaintextToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        await DeletePendingAsync(
            userId,
            AuthenticationEmailMessageKind.PasswordReset,
            cancellationToken);
        return StagePasswordReset(userId, plaintextToken, expiresAt);
    }

    private AuthenticationEmailOutboxMessage Stage(
        long userId,
        AuthenticationEmailMessageKind kind,
        string plaintextToken,
        DateTimeOffset expiresAt)
    {
        var createdAt = _timeProvider.GetUtcNow();
        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                "An authentication email message must expire in the future.");
        }

        var protectedPayload = _protection.Protect(
            userId,
            kind,
            plaintextToken);
        var message = new AuthenticationEmailOutboxMessage
        {
            UserId = userId,
            Kind = kind,
            PayloadCiphertext = protectedPayload.Ciphertext,
            PayloadNonce = protectedPayload.Nonce,
            PayloadAuthenticationTag = protectedPayload.AuthenticationTag,
            PayloadKeyId = protectedPayload.KeyId,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            NextAttemptAt = createdAt,
        };

        // Deliberately do not call SaveChanges. The account operation that stages this
        // message owns the transaction, so its state change and the Outbox insert commit
        // or roll back together.
        _db.AuthenticationEmailOutboxMessages.Add(message);
        return message;
    }

    private async Task DeletePendingAsync(
        long userId,
        AuthenticationEmailMessageKind kind,
        CancellationToken cancellationToken)
    {
        await _db.AuthenticationEmailOutboxMessages
            .Where(message =>
                message.UserId == userId
                && message.Kind == kind)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static void ValidateToken(
        string plaintextToken,
        int encodedLength,
        int entropySize,
        string purpose)
    {
        if (string.IsNullOrEmpty(plaintextToken)
            || plaintextToken.Length != encodedLength)
        {
            throw new ArgumentException(
                $"A valid {purpose} token is required.",
                nameof(plaintextToken));
        }

        byte[] decoded;
        try
        {
            decoded = WebEncoders.Base64UrlDecode(plaintextToken);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"A valid {purpose} token is required.",
                nameof(plaintextToken),
                exception);
        }

        try
        {
            if (decoded.Length != entropySize)
            {
                throw new ArgumentException(
                    $"A valid {purpose} token is required.",
                    nameof(plaintextToken));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }
}
