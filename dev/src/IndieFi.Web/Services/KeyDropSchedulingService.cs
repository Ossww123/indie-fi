using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Services;

public enum ScheduleKeyDropOutcome
{
    Scheduled,
    GameNotFound,
    NotOwner,
    NoKeys,
    OpenTimeNotFuture,
}

public sealed record ScheduleKeyDropResult(
    ScheduleKeyDropOutcome Outcome,
    long? KeyDropId = null,
    int KeyCount = 0);

public sealed class KeyDropSchedulingService
{
    private readonly AppDbContext _db;

    public KeyDropSchedulingService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ScheduleKeyDropResult> ScheduleAsync(
        long gameId,
        long ownerId,
        DateTimeOffset opensAt,
        string keysText)
    {
        var gameOwnerId = await _db.Games
            .Where(game => game.Id == gameId)
            .Select(game => (long?)game.OwnerId)
            .SingleOrDefaultAsync();

        if (gameOwnerId is null)
        {
            return new ScheduleKeyDropResult(ScheduleKeyDropOutcome.GameNotFound);
        }

        if (gameOwnerId != ownerId)
        {
            return new ScheduleKeyDropResult(ScheduleKeyDropOutcome.NotOwner);
        }

        var keys = ParseKeys(keysText);
        if (keys.Count == 0)
        {
            return new ScheduleKeyDropResult(ScheduleKeyDropOutcome.NoKeys);
        }

        if (opensAt <= DateTimeOffset.Now)
        {
            return new ScheduleKeyDropResult(ScheduleKeyDropOutcome.OpenTimeNotFuture);
        }

        var keyDrop = new KeyDrop
        {
            GameId = gameId,
            OwnerId = ownerId,
            OpensAt = opensAt.ToUniversalTime(),
            TotalKeys = keys.Count,
        };

        var steamKeys = keys.Select(key => new SteamKey
        {
            Drop = keyDrop,
            KeyValue = key,
        });

        _db.KeyDrops.Add(keyDrop);
        _db.SteamKeys.AddRange(steamKeys);
        await _db.SaveChangesAsync();

        return new ScheduleKeyDropResult(
            ScheduleKeyDropOutcome.Scheduled,
            keyDrop.Id,
            keys.Count);
    }

    private static List<string> ParseKeys(string keysText)
    {
        return keysText
            .Split('\n')
            .Select(key => key.Trim())
            .Where(key => key.Length > 0)
            .ToList();
    }
}
