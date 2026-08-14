using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.AccountMigration;

public sealed class EmailProtectionMigration
{
    private readonly AppDbContext _db;
    private readonly EmailProtectionService _emailProtection;

    public EmailProtectionMigration(
        AppDbContext db,
        EmailProtectionService emailProtection)
    {
        _db = db;
        _emailProtection = emailProtection;
    }

    public async Task<EmailProtectionBackfillResult> BackfillAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 10_000);

        long lastUserId = 0;
        long scannedUserCount = 0;
        long protectedUserCount = 0;
        long addedLookupCount = 0;
        long problemUserCount = 0;

        while (true)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                cancellationToken);
            var users = await _db.Users
                .Where(user => user.Id > lastUserId)
                .OrderBy(user => user.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (users.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                break;
            }

            var userIds = users.Select(user => user.Id).ToArray();
            var lookups = await _db.UserEmailLookups
                .Where(lookup => userIds.Contains(lookup.UserId))
                .ToListAsync(cancellationToken);

            foreach (var user in users)
            {
                scannedUserCount++;

                if (!TryPrepareUser(user, lookups, out var prepared))
                {
                    problemUserCount++;
                    continue;
                }

                if (prepared.ProtectedEmail is not null)
                {
                    user.EmailCiphertext = prepared.ProtectedEmail.Ciphertext;
                    user.EmailNonce = prepared.ProtectedEmail.Nonce;
                    user.EmailAuthenticationTag = prepared.ProtectedEmail.AuthenticationTag;
                    user.EmailEncryptionKeyId = prepared.ProtectedEmail.KeyId;
                    protectedUserCount++;
                }

                foreach (var lookup in prepared.MissingLookups)
                {
                    _db.UserEmailLookups.Add(new UserEmailLookup
                    {
                        UserId = user.Id,
                        KeyId = lookup.KeyId,
                        Value = lookup.Value,
                    });
                    addedLookupCount++;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            lastUserId = users[^1].Id;
            _db.ChangeTracker.Clear();
        }

        return new EmailProtectionBackfillResult(
            scannedUserCount,
            protectedUserCount,
            addedLookupCount,
            problemUserCount);
    }

    public async Task<EmailProtectionVerificationResult> VerifyAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 10_000);

        long lastUserId = 0;
        long scannedUserCount = 0;
        long invalidPlaintextEmailCount = 0;
        long normalizedEmailMismatchCount = 0;
        long incompleteProtectionCount = 0;
        long unreadableProtectionCount = 0;
        long decryptedEmailMismatchCount = 0;
        long missingLookupCount = 0;
        long lookupMismatchCount = 0;

        while (true)
        {
            var users = await _db.Users
                .AsNoTracking()
                .Where(user => user.Id > lastUserId)
                .OrderBy(user => user.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (users.Count == 0)
            {
                break;
            }

            var userIds = users.Select(user => user.Id).ToArray();
            var lookups = await _db.UserEmailLookups
                .AsNoTracking()
                .Where(lookup => userIds.Contains(lookup.UserId))
                .ToListAsync(cancellationToken);

            foreach (var user in users)
            {
                scannedUserCount++;
                AccountEmailAddress? accountEmail = null;

                if (!AccountEmailAddressPolicy.TryCreate(
                        user.Email,
                        out accountEmail,
                        out _))
                {
                    invalidPlaintextEmailCount++;
                }
                else if (!string.Equals(
                             accountEmail!.NormalizedValue,
                             user.NormalizedEmail,
                             StringComparison.Ordinal))
                {
                    normalizedEmailMismatchCount++;
                }

                if (!HasCompleteProtection(user))
                {
                    incompleteProtectionCount++;
                }
                else
                {
                    try
                    {
                        var decryptedEmail = _emailProtection.Unprotect(
                            ToProtectedEmail(user));
                        if (!string.Equals(
                                decryptedEmail,
                                user.Email,
                                StringComparison.Ordinal))
                        {
                            decryptedEmailMismatchCount++;
                        }
                    }
                    catch (CryptographicException)
                    {
                        unreadableProtectionCount++;
                    }
                    catch (InvalidOperationException)
                    {
                        unreadableProtectionCount++;
                    }
                }

                if (accountEmail is null)
                {
                    continue;
                }

                var actualByKeyId = lookups
                    .Where(lookup => lookup.UserId == user.Id)
                    .ToDictionary(lookup => lookup.KeyId, StringComparer.Ordinal);
                foreach (var expected in
                         _emailProtection.CreateWritableLookupValues(accountEmail))
                {
                    if (!actualByKeyId.TryGetValue(expected.KeyId, out var actual))
                    {
                        missingLookupCount++;
                    }
                    else if (!CryptographicOperations.FixedTimeEquals(
                                 expected.Value,
                                 actual.Value))
                    {
                        lookupMismatchCount++;
                    }
                }
            }

            lastUserId = users[^1].Id;
        }

        var duplicateLookupGroupCount = await _db.UserEmailLookups
            .AsNoTracking()
            .GroupBy(lookup => new { lookup.KeyId, lookup.Value })
            .Where(group => group.Count() > 1)
            .LongCountAsync(cancellationToken);

        return new EmailProtectionVerificationResult(
            scannedUserCount,
            invalidPlaintextEmailCount,
            normalizedEmailMismatchCount,
            incompleteProtectionCount,
            unreadableProtectionCount,
            decryptedEmailMismatchCount,
            missingLookupCount,
            lookupMismatchCount,
            duplicateLookupGroupCount);
    }

    private bool TryPrepareUser(
        User user,
        IReadOnlyList<UserEmailLookup> lookups,
        out PreparedUser prepared)
    {
        prepared = PreparedUser.Empty;

        if (!AccountEmailAddressPolicy.TryCreate(
                user.Email,
                out var accountEmail,
                out _)
            || !string.Equals(
                accountEmail!.NormalizedValue,
                user.NormalizedEmail,
                StringComparison.Ordinal))
        {
            return false;
        }

        var hasAnyProtection = HasAnyProtection(user);
        var hasCompleteProtection = HasCompleteProtection(user);
        if (hasAnyProtection && !hasCompleteProtection)
        {
            return false;
        }

        if (hasCompleteProtection)
        {
            try
            {
                var decryptedEmail = _emailProtection.Unprotect(ToProtectedEmail(user));
                if (!string.Equals(
                        decryptedEmail,
                        user.Email,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var actualByKeyId = lookups
            .Where(lookup => lookup.UserId == user.Id)
            .ToDictionary(lookup => lookup.KeyId, StringComparer.Ordinal);
        var missingLookups = new List<EmailLookupValue>();

        foreach (var expected in
                 _emailProtection.CreateWritableLookupValues(accountEmail))
        {
            if (!actualByKeyId.TryGetValue(expected.KeyId, out var actual))
            {
                missingLookups.Add(expected);
            }
            else if (!CryptographicOperations.FixedTimeEquals(
                         expected.Value,
                         actual.Value))
            {
                return false;
            }
        }

        prepared = new PreparedUser(
            hasCompleteProtection ? null : _emailProtection.Protect(accountEmail),
            missingLookups);
        return true;
    }

    private static bool HasAnyProtection(User user)
    {
        return user.EmailCiphertext is not null
            || user.EmailNonce is not null
            || user.EmailAuthenticationTag is not null
            || user.EmailEncryptionKeyId is not null;
    }

    private static bool HasCompleteProtection(User user)
    {
        return user.EmailCiphertext is not null
            && user.EmailNonce is not null
            && user.EmailAuthenticationTag is not null
            && user.EmailEncryptionKeyId is not null;
    }

    private static ProtectedEmailAddress ToProtectedEmail(User user)
    {
        return new ProtectedEmailAddress(
            user.EmailCiphertext!,
            user.EmailNonce!,
            user.EmailAuthenticationTag!,
            user.EmailEncryptionKeyId!);
    }

    private sealed record PreparedUser(
        ProtectedEmailAddress? ProtectedEmail,
        IReadOnlyList<EmailLookupValue> MissingLookups)
    {
        public static readonly PreparedUser Empty = new(null, []);
    }
}

public sealed record EmailProtectionBackfillResult(
    long ScannedUserCount,
    long ProtectedUserCount,
    long AddedLookupCount,
    long ProblemUserCount)
{
    public bool IsSuccessful => ProblemUserCount == 0;
}

public sealed record EmailProtectionVerificationResult(
    long ScannedUserCount,
    long InvalidPlaintextEmailCount,
    long NormalizedEmailMismatchCount,
    long IncompleteProtectionCount,
    long UnreadableProtectionCount,
    long DecryptedEmailMismatchCount,
    long MissingLookupCount,
    long LookupMismatchCount,
    long DuplicateLookupGroupCount)
{
    public bool IsSuccessful =>
        InvalidPlaintextEmailCount == 0
        && NormalizedEmailMismatchCount == 0
        && IncompleteProtectionCount == 0
        && UnreadableProtectionCount == 0
        && DecryptedEmailMismatchCount == 0
        && MissingLookupCount == 0
        && LookupMismatchCount == 0
        && DuplicateLookupGroupCount == 0;
}
