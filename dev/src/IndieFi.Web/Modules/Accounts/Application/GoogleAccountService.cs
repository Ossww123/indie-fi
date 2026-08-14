using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public enum GoogleAccountOutcome
{
    Authenticated,
    Unlinked,
}

public sealed record GoogleAccountResult(
    GoogleAccountOutcome Outcome,
    long? UserId = null,
    long? SessionVersion = null);

public sealed class GoogleAccountService
{
    private readonly AppDbContext _db;

    public GoogleAccountService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<GoogleAccountResult> ResolveAsync(
        string providerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUserId);

        var account = await (
            from login in _db.ExternalLogins.AsNoTracking()
            join user in _db.Users.AsNoTracking() on login.UserId equals user.Id
            where login.Provider == ExternalLoginPolicy.GoogleProvider
                && login.ProviderUserId == providerUserId
            select new { user.Id, user.SessionVersion })
            .SingleOrDefaultAsync(cancellationToken);

        return account is null
            ? new GoogleAccountResult(GoogleAccountOutcome.Unlinked)
            : new GoogleAccountResult(
                GoogleAccountOutcome.Authenticated,
                account.Id,
                account.SessionVersion);
    }
}
