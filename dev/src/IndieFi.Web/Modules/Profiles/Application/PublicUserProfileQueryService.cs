using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Profiles.Application;

public sealed record PublicProfileGame(
    long Id,
    string Title,
    string? Description,
    DateTimeOffset CreatedAt);

public sealed record PublicUserProfile(
    long UserId,
    string DisplayName,
    string Biography,
    int CompletedReviewCount,
    IReadOnlyList<PublicProfileGame> Games);

public sealed class PublicUserProfileQueryService
{
    private readonly AppDbContext _db;

    public PublicUserProfileQueryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PublicUserProfile?> GetAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return null;
        }

        var header = await (
                from user in _db.Users.AsNoTracking()
                join profile in _db.UserProfiles.AsNoTracking()
                    on user.Id equals profile.UserId into profileRows
                from profile in profileRows.DefaultIfEmpty()
                where user.Id == userId
                select new PublicProfileHeader(
                    user.Id,
                    profile == null ? null : profile.Nickname,
                    profile == null ? string.Empty : profile.Biography))
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var games = await _db.Games
            .AsNoTracking()
            .Where(game => game.OwnerId == userId)
            .OrderByDescending(game => game.CreatedAt)
            .ThenByDescending(game => game.Id)
            .Select(game => new PublicProfileGame(
                game.Id,
                game.Title,
                game.Description,
                game.CreatedAt))
            .ToListAsync(cancellationToken);
        var completedReviewCount = await _db.FeedbackSubmissions
            .AsNoTracking()
            .CountAsync(
                submission => submission.ReviewerId == userId,
                cancellationToken);

        return new PublicUserProfile(
            header.UserId,
            UserDisplayName.Resolve(header.UserId, header.Nickname),
            header.Biography,
            completedReviewCount,
            games);
    }

    private sealed record PublicProfileHeader(
        long UserId,
        string? Nickname,
        string Biography);
}
