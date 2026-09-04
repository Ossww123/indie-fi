using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed record AccountSessionValidationResult(string DisplayName);

/// <summary>
/// Checks the server-owned account state represented by an authentication ticket.
/// </summary>
public sealed class AccountSessionValidationService
{
    private readonly AppDbContext _db;

    public AccountSessionValidationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsValidAsync(
        long userId,
        long sessionVersion,
        CancellationToken cancellationToken = default) =>
        await ValidateAsync(userId, sessionVersion, cancellationToken) is not null;

    public async Task<AccountSessionValidationResult?> ValidateAsync(
        long userId,
        long sessionVersion,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || sessionVersion <= 0)
        {
            return null;
        }

        var account = await (
                from user in _db.Users.AsNoTracking()
                join profile in _db.UserProfiles.AsNoTracking()
                    on user.Id equals profile.UserId into profileRows
                from profile in profileRows.DefaultIfEmpty()
                where user.Id == userId
                    && user.EmailConfirmedAt != null
                    && user.AccountStatus == AccountStatus.Active
                    && user.SessionVersion == sessionVersion
                select new
                {
                    user.Id,
                    Nickname = profile == null ? null : profile.Nickname,
                })
            .SingleOrDefaultAsync(cancellationToken);

        return account is null
            ? null
            : new AccountSessionValidationResult(
                UserDisplayName.Resolve(account.Id, account.Nickname));
    }
}
