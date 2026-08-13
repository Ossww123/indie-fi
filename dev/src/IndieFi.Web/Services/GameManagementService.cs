using IndieFi.Web.Data;
using IndieFi.Web.Entities;

namespace IndieFi.Web.Services;

public enum UpdateGameOutcome
{
    Updated,
    NotFound,
}

public sealed record CreateGameResult(long GameId, string Title);

public sealed record UpdateGameResult(UpdateGameOutcome Outcome);

public sealed class GameManagementService
{
    private readonly AppDbContext _db;

    public GameManagementService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CreateGameResult> CreateAsync(
        long ownerId,
        string title,
        string? description)
    {
        var game = new Game
        {
            OwnerId = ownerId,
            Title = title,
            Description = NormalizeDescription(description),
        };

        _db.Games.Add(game);
        await _db.SaveChangesAsync();

        return new CreateGameResult(game.Id, game.Title);
    }

    public async Task<UpdateGameResult> UpdateAsync(
        long gameId,
        long ownerId,
        string title,
        string? description)
    {
        var game = await _db.Games.FindAsync(gameId);

        if (game is null || game.OwnerId != ownerId)
        {
            return new UpdateGameResult(UpdateGameOutcome.NotFound);
        }

        game.Title = title;
        game.Description = NormalizeDescription(description);
        await _db.SaveChangesAsync();

        return new UpdateGameResult(UpdateGameOutcome.Updated);
    }

    private static string? NormalizeDescription(string? description)
    {
        return string.IsNullOrWhiteSpace(description) ? null : description;
    }
}
