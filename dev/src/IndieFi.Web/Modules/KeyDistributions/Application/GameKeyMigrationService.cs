using System.Security.Cryptography;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class GameKeyMigrationService
{
    public const int MaximumBatchSize = 1_000;

    private const string FingerprintUniqueConstraint =
        "IX_game_key_fingerprints_key_id_value";

    private readonly AppDbContext _db;
    private readonly IKeyProvider _keyProvider;
    private readonly GameKeyProtectionService _gameKeyProtection;

    public GameKeyMigrationService(
        AppDbContext db,
        IKeyProvider keyProvider)
    {
        _db = db;
        _keyProvider = keyProvider;
        _gameKeyProtection = new GameKeyProtectionService(keyProvider);
    }

    public async Task<GameKeyMigrationBatchResult> MigrateBatchAsync(
        long afterGameKeyId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ValidateCursorAndBatchSize(afterGameKeyId, batchSize);

        var currentEncryptionKeyId = _keyProvider
            .GetCurrent(KeyPurpose.GameKeyEncryption)
            .Id;
        var writableFingerprintKeyIds = GetWritableFingerprintKeyIds();

        await using var transaction = await _db.Database.BeginTransactionAsync(
            cancellationToken);
        var gameKeys = await _db.GameKeys
            .FromSqlInterpolated($$"""
                SELECT *
                FROM game_keys
                WHERE id > {{afterGameKeyId}}
                ORDER BY id
                LIMIT {{batchSize}}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (gameKeys.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new GameKeyMigrationBatchResult(
                afterGameKeyId,
                0,
                0,
                0,
                0);
        }

        var gameKeyIds = gameKeys.Select(gameKey => gameKey.Id).ToArray();
        var storedFingerprints = await _db.GameKeyFingerprints
            .Where(fingerprint =>
                gameKeyIds.Contains(fingerprint.GameKeyId)
                && writableFingerprintKeyIds.Contains(fingerprint.KeyId))
            .ToListAsync(cancellationToken);
        var fingerprintByGameKeyAndKey = storedFingerprints.ToDictionary(
            fingerprint => (fingerprint.GameKeyId, fingerprint.KeyId));
        var expectedFingerprintOwners = new Dictionary<
            (string KeyId, string Value),
            long>();

        var encryptedCount = 0;
        var reencryptedCount = 0;
        var addedFingerprintCount = 0;

        foreach (var gameKey in gameKeys)
        {
            var value = ReadGameKey(gameKey);

            foreach (var expectedFingerprint in
                     _gameKeyProtection.CreateWritableFingerprints(value))
            {
                var expectedIdentity = (
                    expectedFingerprint.KeyId,
                    Convert.ToHexString(expectedFingerprint.Value));
                if (expectedFingerprintOwners.TryGetValue(
                        expectedIdentity,
                        out var otherGameKeyId)
                    && otherGameKeyId != gameKey.Id)
                {
                    throw DuplicateGameKeyInBatch(
                        otherGameKeyId,
                        gameKey.Id);
                }

                expectedFingerprintOwners[expectedIdentity] = gameKey.Id;

                if (fingerprintByGameKeyAndKey.TryGetValue(
                        (gameKey.Id, expectedFingerprint.KeyId),
                        out var storedFingerprint))
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            storedFingerprint.Value,
                            expectedFingerprint.Value))
                    {
                        throw new InvalidOperationException(
                            $"Game key {gameKey.Id} has an invalid fingerprint for "
                            + $"key '{expectedFingerprint.KeyId}'.");
                    }

                    continue;
                }

                var newFingerprint = new StoredGameKeyFingerprint
                {
                    GameKeyId = gameKey.Id,
                    KeyId = expectedFingerprint.KeyId,
                    Value = expectedFingerprint.Value,
                };
                _db.GameKeyFingerprints.Add(newFingerprint);
                fingerprintByGameKeyAndKey.Add(
                    (newFingerprint.GameKeyId, newFingerprint.KeyId),
                    newFingerprint);
                addedFingerprintCount++;
            }

            if (string.Equals(
                    gameKey.EncryptionKeyId,
                    currentEncryptionKeyId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var wasEncrypted = HasCompleteEncryption(gameKey);
            var protectedKey = _gameKeyProtection.Protect(value);
            gameKey.Ciphertext = protectedKey.Ciphertext;
            gameKey.Nonce = protectedKey.Nonce;
            gameKey.AuthenticationTag = protectedKey.AuthenticationTag;
            gameKey.EncryptionKeyId = protectedKey.EncryptionKeyId;

            if (wasEncrypted)
            {
                reencryptedCount++;
            }
            else
            {
                encryptedCount++;
            }
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (IsFingerprintUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            throw new InvalidOperationException(
                $"A duplicate game key was detected while processing game key IDs "
                + $"{gameKeys[0].Id} through {gameKeys[^1].Id}.");
        }

        return new GameKeyMigrationBatchResult(
            gameKeys[^1].Id,
            gameKeys.Count,
            encryptedCount,
            reencryptedCount,
            addedFingerprintCount);
    }

    public async Task<GameKeyVerificationBatchResult> VerifyBatchAsync(
        long afterGameKeyId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ValidateCursorAndBatchSize(afterGameKeyId, batchSize);

        var currentEncryptionKeyId = _keyProvider
            .GetCurrent(KeyPurpose.GameKeyEncryption)
            .Id;
        var writableFingerprintKeyIds = GetWritableFingerprintKeyIds();
        var gameKeys = await _db.GameKeys
            .AsNoTracking()
            .Where(gameKey => gameKey.Id > afterGameKeyId)
            .OrderBy(gameKey => gameKey.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (gameKeys.Count == 0)
        {
            return new GameKeyVerificationBatchResult(afterGameKeyId, 0);
        }

        var gameKeyIds = gameKeys.Select(gameKey => gameKey.Id).ToArray();
        var fingerprints = await _db.GameKeyFingerprints
            .AsNoTracking()
            .Where(fingerprint =>
                gameKeyIds.Contains(fingerprint.GameKeyId)
                && writableFingerprintKeyIds.Contains(fingerprint.KeyId))
            .ToDictionaryAsync(
                fingerprint => (fingerprint.GameKeyId, fingerprint.KeyId),
                cancellationToken);

        foreach (var gameKey in gameKeys)
        {
            if (!HasCompleteEncryption(gameKey)
                || !string.Equals(
                    gameKey.EncryptionKeyId,
                    currentEncryptionKeyId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Game key {gameKey.Id} is not encrypted with the current key.");
            }

            var value = ReadGameKey(gameKey);
            foreach (var expectedFingerprint in
                     _gameKeyProtection.CreateWritableFingerprints(value))
            {
                if (!fingerprints.TryGetValue(
                        (gameKey.Id, expectedFingerprint.KeyId),
                        out var storedFingerprint)
                    || !CryptographicOperations.FixedTimeEquals(
                        storedFingerprint.Value,
                        expectedFingerprint.Value))
                {
                    throw new InvalidOperationException(
                        $"Game key {gameKey.Id} is missing a valid fingerprint for "
                        + $"key '{expectedFingerprint.KeyId}'.");
                }
            }
        }

        return new GameKeyVerificationBatchResult(
            gameKeys[^1].Id,
            gameKeys.Count);
    }

    public async Task<int> VerifyAllAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ValidateCursorAndBatchSize(0, batchSize);

        long cursor = 0;
        var verifiedCount = 0;
        while (true)
        {
            var result = await VerifyBatchAsync(
                cursor,
                batchSize,
                cancellationToken);
            if (result.ScannedCount == 0)
            {
                return verifiedCount;
            }

            cursor = result.NextGameKeyId;
            verifiedCount += result.ScannedCount;
        }
    }

    public async Task<int> RetireFingerprintKeyAsync(
        string retiredKeyId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retiredKeyId);
        if (retiredKeyId.Length > 100)
        {
            throw new ArgumentException(
                "A retired fingerprint key ID cannot exceed 100 characters.",
                nameof(retiredKeyId));
        }

        EnsureFingerprintKeyIsRetired(retiredKeyId);
        await VerifyAllAsync(batchSize, cancellationToken);

        var deletedCount = 0;
        while (true)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                cancellationToken);
            var fingerprints = await _db.GameKeyFingerprints
                .FromSqlInterpolated($$"""
                    SELECT *
                    FROM game_key_fingerprints
                    WHERE key_id = {{retiredKeyId}}
                    ORDER BY game_key_id
                    LIMIT {{batchSize}}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);

            if (fingerprints.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return deletedCount;
            }

            _db.GameKeyFingerprints.RemoveRange(fingerprints);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            deletedCount += fingerprints.Count;
        }
    }

    private GameKeyValue ReadGameKey(GameKey gameKey)
    {
        var hasAnyEncryption = gameKey.Ciphertext is not null
            || gameKey.Nonce is not null
            || gameKey.AuthenticationTag is not null
            || gameKey.EncryptionKeyId is not null;
        GameKeyValue value;

        if (hasAnyEncryption)
        {
            if (!HasCompleteEncryption(gameKey))
            {
                throw new InvalidOperationException(
                    $"Game key {gameKey.Id} has incomplete encryption metadata.");
            }

            value = _gameKeyProtection.Unprotect(new ProtectedGameKey(
                gameKey.Ciphertext!,
                gameKey.Nonce!,
                gameKey.AuthenticationTag!,
                gameKey.EncryptionKeyId!));
        }
        else
        {
            if (gameKey.KeyValue is null)
            {
                throw new InvalidOperationException(
                    $"Game key {gameKey.Id} has no stored key material.");
            }

            try
            {
                value = new GameKeyValue(gameKey.KeyValue);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Game key {gameKey.Id} violates the game key policy.");
            }
        }

        if (gameKey.KeyValue is not null
            && !string.Equals(
                gameKey.KeyValue,
                value.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Game key {gameKey.Id} has mismatched plaintext and ciphertext.");
        }

        return value;
    }

    private string[] GetWritableFingerprintKeyIds()
    {
        return _keyProvider
            .GetWritable(KeyPurpose.GameKeyFingerprint)
            .Select(key => key.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void EnsureFingerprintKeyIsRetired(string keyId)
    {
        var currentKeyId = _keyProvider
            .GetCurrent(KeyPurpose.GameKeyFingerprint)
            .Id;
        var activeKeyIds = _keyProvider
            .GetReadable(KeyPurpose.GameKeyFingerprint)
            .Concat(_keyProvider.GetWritable(KeyPurpose.GameKeyFingerprint))
            .Select(key => key.Id)
            .Append(currentKeyId);

        if (activeKeyIds.Contains(keyId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fingerprint key '{keyId}' is still current, readable, or writable.");
        }
    }

    private static bool HasCompleteEncryption(GameKey gameKey)
    {
        return gameKey.Ciphertext is not null
            && gameKey.Nonce is not null
            && gameKey.AuthenticationTag is not null
            && gameKey.EncryptionKeyId is not null;
    }

    private static InvalidOperationException DuplicateGameKeyInBatch(
        long firstGameKeyId,
        long secondGameKeyId)
    {
        return new InvalidOperationException(
            $"Duplicate game keys were detected at game key IDs "
            + $"{firstGameKeyId} and {secondGameKeyId}.");
    }

    private static bool IsFingerprintUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(
                postgresException.ConstraintName,
                FingerprintUniqueConstraint,
                StringComparison.Ordinal);
    }

    private static void ValidateCursorAndBatchSize(
        long afterGameKeyId,
        int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterGameKeyId);
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                $"Batch size must be between 1 and {MaximumBatchSize}.");
        }
    }
}

public sealed record GameKeyMigrationBatchResult(
    long NextGameKeyId,
    int ScannedCount,
    int EncryptedCount,
    int ReencryptedCount,
    int AddedFingerprintCount);

public sealed record GameKeyVerificationBatchResult(
    long NextGameKeyId,
    int ScannedCount);
