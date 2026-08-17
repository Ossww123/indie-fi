using IndieFi.Web.Application.Validation;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Games.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IndieFi.Web.Pages.Games;

public abstract class GameFormPageModel : PageModel
{
    // The services own validation rules so every caller receives the same result.
    // Page properties stay annotation-free and only carry bound form values.
    [BindProperty]
    public string? Title { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public string? GenreCode { get; set; }

    [BindProperty]
    public List<string> TagCodes { get; set; } = [];

    [BindProperty]
    public List<string> PlatformCodes { get; set; } = [];

    [BindProperty]
    public List<GamePlayLinkFormInput> Links { get; set; } = [];

    public GameClassificationSelectionCatalog ClassificationCatalog { get; private set; } =
        new([], [], []);

    protected GameClassificationInput CreateClassificationInput()
    {
        return new GameClassificationInput(GenreCode, TagCodes, PlatformCodes);
    }

    protected async Task LoadActiveClassificationCatalogAsync(
        GameClassificationCatalogQueryService service,
        CancellationToken cancellationToken)
    {
        ClassificationCatalog = await service.GetActiveAsync(cancellationToken);
    }

    protected async Task<bool> LoadEditorClassificationCatalogAsync(
        GameClassificationCatalogQueryService service,
        long gameId,
        bool loadSelections,
        CancellationToken cancellationToken)
    {
        var editor = await service.GetForGameAsync(gameId, cancellationToken);
        if (editor is null)
        {
            return false;
        }

        ClassificationCatalog = editor.Options;
        if (loadSelections)
        {
            GenreCode = editor.Options.Genres
                .Single(item => item.Id == editor.SelectedGenreId)
                .Code;
            TagCodes = editor.Options.Tags
                .Where(item => editor.SelectedTagIds.Contains(item.Id))
                .Select(item => item.Code)
                .ToList();
            PlatformCodes = editor.Options.Platforms
                .Where(item => editor.SelectedPlatformIds.Contains(item.Id))
                .Select(item => item.Code)
                .ToList();
        }

        return true;
    }

    protected IReadOnlyList<GamePlayLinkInput> CreateLinkInputs()
    {
        Links.RemoveAll(link => link.IsBlank());

        // A blank starter row represents "no link". Once those rows are removed,
        // discard their bound field keys so Razor uses the compacted model indexes
        // and service errors attach to the row the user actually entered.
        var boundLinkFields = ModelState.Keys
            .Where(key => key.Equals(nameof(Links), StringComparison.Ordinal)
                || key.StartsWith($"{nameof(Links)}[", StringComparison.Ordinal))
            .ToArray();
        foreach (var field in boundLinkFields)
        {
            ModelState.Remove(field);
        }

        return Links
            .Select(link => new GamePlayLinkInput(
                link.DisplayName,
                link.Provider,
                link.TargetPlatform,
                link.Url))
            .ToArray();
    }

    protected void AddViolations(IEnumerable<ValidationViolation> violations)
    {
        foreach (var violation in violations)
        {
            ModelState.AddModelError(violation.Field, violation.Message);
        }
    }

    protected void EnsureLinkEditorRow()
    {
        if (Links.Count == 0)
        {
            Links.Add(new GamePlayLinkFormInput());
        }
    }
}

public sealed class GamePlayLinkFormInput
{
    public string? DisplayName { get; set; }

    public GamePlayLinkProvider Provider { get; set; } = GamePlayLinkProvider.Other;

    public GamePlayLinkPlatform? TargetPlatform { get; set; }

    public string? Url { get; set; }

    public bool IsBlank()
    {
        return string.IsNullOrWhiteSpace(DisplayName)
            && string.IsNullOrWhiteSpace(Url);
    }
}
