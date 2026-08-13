using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Services;

public enum ScheduleKeyDropOutcome
{
    Scheduled,
    GameNotFound,
    NotOwner,
    Invalid,
}

public sealed record ScheduleKeyDropResult(
    ScheduleKeyDropOutcome Outcome,
    IReadOnlyList<ValidationViolation> Violations,
    long? KeyDropId = null,
    int KeyCount = 0);

public sealed class KeyDropSchedulingService
{
    public const string OpensAtField = "OpensAt";
    public const string KeysField = "KeysText";

    private static readonly IReadOnlyList<ValidationViolation> NoViolations = [];

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
            return new ScheduleKeyDropResult(
                ScheduleKeyDropOutcome.GameNotFound,
                NoViolations);
        }

        if (gameOwnerId != ownerId)
        {
            return new ScheduleKeyDropResult(
                ScheduleKeyDropOutcome.NotOwner,
                NoViolations);
        }

        var keys = ParseKeys(keysText);

        // Both rules are checked before returning so a form with two mistakes reports
        // both at once rather than one per submission.
        var violations = new List<ValidationViolation>();

        if (keys.Count == 0)
        {
            violations.Add(new ValidationViolation(
                KeysField,
                "키를 한 개 이상 입력해 주세요."));
        }

        if (opensAt <= DateTimeOffset.Now)
        {
            violations.Add(new ValidationViolation(
                OpensAtField,
                "오픈 시각은 현재보다 미래여야 합니다."));
        }

        if (violations.Count > 0)
        {
            return new ScheduleKeyDropResult(ScheduleKeyDropOutcome.Invalid, violations);
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
            NoViolations,
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
