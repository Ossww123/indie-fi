using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Accounts.Application;

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
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || sessionVersion <= 0)
        {
            return false;
        }

        return await _db.Users
            .AsNoTracking()
            .AnyAsync(
                user =>
                    user.Id == userId
                    && user.EmailConfirmedAt != null
                    && user.AccountStatus == AccountStatus.Active
                    && user.SessionVersion == sessionVersion,
                cancellationToken);
    }
}
