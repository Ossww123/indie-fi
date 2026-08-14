using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed class AccountEmailKeyRotationService
{
    public const int MaximumBatchSize = 1_000;

    private readonly AppDbContext _db;
    private readonly IKeyProvider _keyProvider;
    private readonly EmailProtectionService _emailProtection;

    public AccountEmailKeyRotationService(
        AppDbContext db,
        IKeyProvider keyProvider)
    {
        _db = db;
        _keyProvider = keyProvider;
        _emailProtection = new EmailProtectionService(keyProvider);
    }

    public async Task<AccountEmailKeyRotationBatchResult> RotateBatchAsync(
        long afterUserId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ValidateCursorAndBatchSize(afterUserId, batchSize);

        var currentEncryptionKeyId = _keyProvider
            .GetCurrent(KeyPurpose.AccountEmailEncryption)
            .Id;
        var writableLookupKeyIds = GetWritableLookupKeyIds();

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var users = await _db.Users
            .FromSqlInterpolated($$"""
                SELECT *
                FROM users
                WHERE id > {{afterUserId}}
                ORDER BY id
                LIMIT {{batchSize}}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (users.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AccountEmailKeyRotationBatchResult(
                afterUserId,
                0,
                0,
                0);
        }

        var userIds = users.Select(user => user.Id).ToArray();
        var storedLookups = await _db.UserEmailLookups
            .Where(lookup =>
                userIds.Contains(lookup.UserId)
                && writableLookupKeyIds.Contains(lookup.KeyId))
            .ToListAsync(cancellationToken);
        var lookupByUserAndKey = storedLookups.ToDictionary(
            lookup => (lookup.UserId, lookup.KeyId));

        var reencryptedCount = 0;
        var addedLookupCount = 0;

        foreach (var user in users)
        {
            var email = ReadEmail(user);

            foreach (var expectedLookup in
                     _emailProtection.CreateWritableLookupValues(email))
            {
                if (lookupByUserAndKey.TryGetValue(
                        (user.Id, expectedLookup.KeyId),
                        out var storedLookup))
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            storedLookup.Value,
                            expectedLookup.Value))
                    {
                        throw new InvalidOperationException(
                            $"User {user.Id} has an invalid lookup for key " +
                            $"'{expectedLookup.KeyId}'.");
                    }

                    continue;
                }

                var newLookup = new UserEmailLookup
                {
                    UserId = user.Id,
                    KeyId = expectedLookup.KeyId,
                    Value = expectedLookup.Value,
                };
                _db.UserEmailLookups.Add(newLookup);
                lookupByUserAndKey.Add(
                    (newLookup.UserId, newLookup.KeyId),
                    newLookup);
                addedLookupCount++;
            }

            if (string.Equals(
                    user.EmailEncryptionKeyId,
                    currentEncryptionKeyId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var protectedEmail = _emailProtection.Protect(email);
            user.EmailCiphertext = protectedEmail.Ciphertext;
            user.EmailNonce = protectedEmail.Nonce;
            user.EmailAuthenticationTag = protectedEmail.AuthenticationTag;
            user.EmailEncryptionKeyId = protectedEmail.KeyId;
            reencryptedCount++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AccountEmailKeyRotationBatchResult(
            users[^1].Id,
            users.Count,
            reencryptedCount,
            addedLookupCount);
    }

    public async Task<AccountEmailKeyVerificationBatchResult> VerifyBatchAsync(
        long afterUserId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ValidateCursorAndBatchSize(afterUserId, batchSize);

        var currentEncryptionKeyId = _keyProvider
            .GetCurrent(KeyPurpose.AccountEmailEncryption)
            .Id;
        var writableLookupKeyIds = GetWritableLookupKeyIds();
        var users = await _db.Users
            .AsNoTracking()
            .Where(user => user.Id > afterUserId)
            .OrderBy(user => user.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (users.Count == 0)
        {
            return new AccountEmailKeyVerificationBatchResult(afterUserId, 0);
        }

        var userIds = users.Select(user => user.Id).ToArray();
        var lookups = await _db.UserEmailLookups
            .AsNoTracking()
            .Where(lookup =>
                userIds.Contains(lookup.UserId)
                && writableLookupKeyIds.Contains(lookup.KeyId))
            .ToDictionaryAsync(
                lookup => (lookup.UserId, lookup.KeyId),
                cancellationToken);

        foreach (var user in users)
        {
            if (!string.Equals(
                    user.EmailEncryptionKeyId,
                    currentEncryptionKeyId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"User {user.Id} is not encrypted with the current email key.");
            }

            var email = ReadEmail(user);
            foreach (var expectedLookup in
                     _emailProtection.CreateWritableLookupValues(email))
            {
                if (!lookups.TryGetValue(
                        (user.Id, expectedLookup.KeyId),
                        out var storedLookup)
                    || !CryptographicOperations.FixedTimeEquals(
                        storedLookup.Value,
                        expectedLookup.Value))
                {
                    throw new InvalidOperationException(
                        $"User {user.Id} is missing a valid lookup for key " +
                        $"'{expectedLookup.KeyId}'.");
                }
            }
        }

        return new AccountEmailKeyVerificationBatchResult(
            users[^1].Id,
            users.Count);
    }

    private AccountEmailAddress ReadEmail(User user)
    {
        var displayEmail = _emailProtection.Unprotect(new ProtectedEmailAddress(
            user.EmailCiphertext,
            user.EmailNonce,
            user.EmailAuthenticationTag,
            user.EmailEncryptionKeyId));

        if (!AccountEmailAddressPolicy.TryCreate(displayEmail, out var email, out _))
        {
            throw new InvalidOperationException(
                $"User {user.Id} has an email that no longer satisfies the account policy.");
        }

        return email!;
    }

    private string[] GetWritableLookupKeyIds()
    {
        return _keyProvider
            .GetWritable(KeyPurpose.AccountEmailLookup)
            .Select(key => key.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateCursorAndBatchSize(long afterUserId, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterUserId);
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                $"Batch size must be between 1 and {MaximumBatchSize}.");
        }
    }
}

public sealed record AccountEmailKeyRotationBatchResult(
    long NextUserId,
    int ScannedCount,
    int ReencryptedCount,
    int AddedLookupCount);

public sealed record AccountEmailKeyVerificationBatchResult(
    long NextUserId,
    int ScannedCount);
