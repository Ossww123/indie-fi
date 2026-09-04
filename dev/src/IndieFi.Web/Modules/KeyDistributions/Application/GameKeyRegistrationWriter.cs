using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class GameKeyRegistrationWriter
{
    private const string UniqueViolation = "23505";
    private const string FingerprintUniqueIndex =
        "IX_game_key_fingerprints_key_id_value";

    private readonly AppDbContext _db;
    private readonly GameKeyProtectionService _gameKeyProtection;

    public GameKeyRegistrationWriter(
        AppDbContext db,
        GameKeyProtectionService gameKeyProtection)
    {
        _db = db;
        _gameKeyProtection = gameKeyProtection;
    }

    public async Task<bool> ContainsRegisteredKeyAsync(
        IReadOnlyList<GameKeyValue> keys,
        CancellationToken cancellationToken = default)
    {
        var readableFingerprints = keys
            .SelectMany(_gameKeyProtection.CreateReadableFingerprints)
            .GroupBy(fingerprint => fingerprint.KeyId, StringComparer.Ordinal);

        foreach (var version in readableFingerprints)
        {
            var valuesParameter = new NpgsqlParameter(
                "values",
                version.Select(fingerprint => fingerprint.Value).ToArray())
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            };
            var keyIdParameter = new NpgsqlParameter("keyId", version.Key);
            var exists = await _db.Database
                .SqlQueryRaw<bool>(
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM game_key_fingerprints
                        WHERE key_id = @keyId
                          AND value = ANY(@values)) AS "Value"
                    """,
                    keyIdParameter,
                    valuesParameter)
                .SingleAsync(cancellationToken);

            if (exists)
            {
                return true;
            }
        }

        return false;
    }

    public void StageForNewDrop(
        KeyDrop drop,
        IReadOnlyList<GameKeyValue> keys)
    {
        ArgumentNullException.ThrowIfNull(drop);
        Stage(keys, () => new GameKey { Drop = drop });
    }

    public void StageForExistingDrop(
        long dropId,
        IReadOnlyList<GameKeyValue> keys)
    {
        Stage(keys, () => new GameKey { DropId = dropId });
    }

    public static bool IsDuplicateFingerprintViolation(
        DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: UniqueViolation,
            ConstraintName: FingerprintUniqueIndex,
        };

    private void Stage(
        IReadOnlyList<GameKeyValue> keys,
        Func<GameKey> createGameKey)
    {
        foreach (var key in keys)
        {
            var protectedKey = _gameKeyProtection.Protect(key);
            var gameKey = createGameKey();
            gameKey.Ciphertext = protectedKey.Ciphertext;
            gameKey.Nonce = protectedKey.Nonce;
            gameKey.AuthenticationTag = protectedKey.AuthenticationTag;
            gameKey.EncryptionKeyId = protectedKey.EncryptionKeyId;
            _db.GameKeys.Add(gameKey);

            foreach (var fingerprint in
                     _gameKeyProtection.CreateWritableFingerprints(key))
            {
                _db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
                {
                    GameKey = gameKey,
                    KeyId = fingerprint.KeyId,
                    Value = fingerprint.Value,
                });
            }
        }
    }
}
