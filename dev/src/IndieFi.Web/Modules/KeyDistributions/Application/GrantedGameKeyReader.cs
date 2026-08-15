using IndieFi.Web.Data;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.KeyDistributions.Application;

public sealed class GrantedGameKeyReader
{
    private readonly AppDbContext _db;
    private readonly GameKeyProtectionService _gameKeyProtection;

    public GrantedGameKeyReader(
        AppDbContext db,
        GameKeyProtectionService gameKeyProtection)
    {
        _db = db;
        _gameKeyProtection = gameKeyProtection;
    }

    public async Task<string?> GetForUserAsync(
        long dropId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var storedKey = await _db.KeyGrants
            .AsNoTracking()
            .Where(grant => grant.DropId == dropId && grant.UserId == userId)
            .Join(
                _db.GameKeys.AsNoTracking(),
                grant => new { Id = grant.KeyId, grant.DropId },
                key => new { Id = key.Id, key.DropId },
                (_, key) => new StoredGrantedGameKey(
                    key.Ciphertext,
                    key.Nonce,
                    key.AuthenticationTag,
                    key.EncryptionKeyId))
            .SingleOrDefaultAsync(cancellationToken);

        if (storedKey is null)
        {
            return null;
        }

        return _gameKeyProtection.Unprotect(new ProtectedGameKey(
            storedKey.Ciphertext,
            storedKey.Nonce,
            storedKey.AuthenticationTag,
            storedKey.EncryptionKeyId)).Value;
    }

    private sealed record StoredGrantedGameKey(
        byte[] Ciphertext,
        byte[] Nonce,
        byte[] AuthenticationTag,
        string EncryptionKeyId)
    {
        public override string ToString() => "[STORED GRANTED GAME KEY]";
    }
}
