using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Profiles.Application;

public sealed class UserDisplayNameResolver
{
    private readonly AppDbContext _db;

    public UserDisplayNameResolver(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string> ResolveAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var nickname = await _db.UserProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => profile.Nickname)
            .SingleOrDefaultAsync(cancellationToken);
        return UserDisplayName.Resolve(userId, nickname);
    }

    public async Task<IReadOnlyDictionary<long, string>> ResolveAsync(
        IEnumerable<long> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var ids = userIds.Distinct().ToArray();
        if (ids.Any(userId => userId <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(userIds),
                "Every user ID must be positive.");
        }

        if (ids.Length == 0)
        {
            return new Dictionary<long, string>();
        }

        var nicknames = await _db.UserProfiles
            .AsNoTracking()
            .Where(profile => ids.Contains(profile.UserId))
            .ToDictionaryAsync(
                profile => profile.UserId,
                profile => profile.Nickname,
                cancellationToken);

        return ids.ToDictionary(
            userId => userId,
            userId => UserDisplayName.Resolve(
                userId,
                nicknames.GetValueOrDefault(userId)));
    }
}
