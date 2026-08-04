using System.ComponentModel.DataAnnotations;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.KeyDrops;

[Authorize]
public class NewModel : PageModel
{
    private readonly AppDbContext _db;

    public NewModel(AppDbContext db)
    {
        _db = db;
    }

    public Game Game { get; private set; } = null!;

    [BindProperty]
    [Required(ErrorMessage = "오픈 시각을 입력해 주세요.")]
    public DateTimeOffset? OpensAt { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "키를 한 개 이상 입력해 주세요.")]
    public string KeysText { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(long gameId)
    {
        var game = await _db.Games.FindAsync(gameId);
        var ownerId = User.GetUserId();

        // Return 404 (not 403) when the game is missing OR not owned by the caller —
        // a 403 would reveal that the game exists.
        if (game is null || game.OwnerId != ownerId)
        {
            return NotFound();
        }

        Game = game;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long gameId)
    {
        var game = await _db.Games.FindAsync(gameId);
        var ownerId = User.GetUserId();

        // The ownership check must be repeated on POST: an attacker can send this
        // POST directly without ever loading the GET page.
        if (game is null || game.OwnerId != ownerId)
        {
            return NotFound();
        }

        Game = game;

        var keys = ParseKeys(KeysText);
        if (keys.Count == 0)
        {
            ModelState.AddModelError(nameof(KeysText), "키를 한 개 이상 입력해 주세요.");
        }

        if (OpensAt is not null && OpensAt <= DateTimeOffset.Now)
        {
            ModelState.AddModelError(nameof(OpensAt), "오픈 시각은 현재보다 미래여야 합니다.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var keyDrop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = ownerId,
            OpensAt = OpensAt!.Value.ToUniversalTime(),
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

        TempData["GameMessage"] = $"드롭 예약 완료 (키 {keys.Count}개)";
        return RedirectToPage("/Games/Detail", new { id = gameId });
    }

    private static List<string> ParseKeys(string? keysText)
    {
        return (keysText ?? string.Empty)
            .Split('\n')
            .Select(key => key.Trim())
            .Where(key => key.Length > 0)
            .ToList();
    }
}
