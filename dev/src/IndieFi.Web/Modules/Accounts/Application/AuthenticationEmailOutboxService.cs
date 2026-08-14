using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.WebUtilities;

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
        ValidateConfirmationToken(plaintextToken);

        var createdAt = _timeProvider.GetUtcNow();
        if (expiresAt <= createdAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                "An authentication email message must expire in the future.");
        }

        var kind = AuthenticationEmailMessageKind.EmailConfirmation;
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
        };

        // Deliberately do not call SaveChanges. The account operation that stages this
        // message owns the transaction, so its state change and the Outbox insert commit
        // or roll back together.
        _db.AuthenticationEmailOutboxMessages.Add(message);
        return message;
    }

    private static void ValidateConfirmationToken(string plaintextToken)
    {
        if (string.IsNullOrEmpty(plaintextToken)
            || plaintextToken.Length != EmailConfirmationTokenService.EncodedTokenLength)
        {
            throw new ArgumentException(
                "A valid email confirmation token is required.",
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
                "A valid email confirmation token is required.",
                nameof(plaintextToken),
                exception);
        }

        try
        {
            if (decoded.Length != EmailConfirmationTokenService.TokenEntropySize)
            {
                throw new ArgumentException(
                    "A valid email confirmation token is required.",
                    nameof(plaintextToken));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }
}
