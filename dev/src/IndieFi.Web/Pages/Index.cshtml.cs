using IndieFi.Web.Application.Validation;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace IndieFi.Web.Pages;

public class IndexModel : PageModel
{
    private readonly PublicGameDiscoveryQueryService _discovery;
    private readonly GameClassificationCatalogQueryService _catalogs;
    private readonly MediaObjectStorageOptions _mediaStorageOptions;

    public IndexModel(
        PublicGameDiscoveryQueryService discovery,
        GameClassificationCatalogQueryService catalogs,
        IOptions<MediaObjectStorageOptions> mediaStorageOptions)
    {
        _discovery = discovery;
        _catalogs = catalogs;
        _mediaStorageOptions = mediaStorageOptions.Value;
    }

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "genre")]
    public string? GenreCode { get; set; }

    [BindProperty(SupportsGet = true, Name = "tags")]
    public string[] TagCodes { get; set; } = [];

    [BindProperty(SupportsGet = true, Name = "platforms")]
    public string[] PlatformCodes { get; set; } = [];

    [BindProperty(SupportsGet = true, Name = "createdFrom")]
    public string? CreatedFrom { get; set; }

    [BindProperty(SupportsGet = true, Name = "createdBefore")]
    public string? CreatedBefore { get; set; }

    [BindProperty(SupportsGet = true, Name = "cursor")]
    public string? Cursor { get; set; }

    public GameClassificationSelectionCatalog Catalog { get; private set; } =
        new([], [], []);
    public IReadOnlyList<PublicGameDiscoveryItem> Games { get; private set; } = [];
    public IReadOnlyList<ValidationViolation> Violations { get; private set; } = [];
    public IReadOnlyDictionary<long, PublicGameMediaImage> Covers { get; private set; } =
        new Dictionary<long, PublicGameMediaImage>();
    public string? NextPageUrl { get; private set; }

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Query)
        || !string.IsNullOrEmpty(GenreCode)
        || TagCodes.Length > 0
        || PlatformCodes.Length > 0
        || !string.IsNullOrEmpty(CreatedFrom)
        || !string.IsNullOrEmpty(CreatedBefore);

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var cancellationToken = HttpContext.RequestAborted;
        Catalog = await _catalogs.GetActiveAsync(cancellationToken);

        var validation = PublicGameDiscoveryInputValidator.Validate(new(
            Query,
            GenreCode,
            TagCodes,
            PlatformCodes,
            CreatedFrom,
            CreatedBefore));
        if (!validation.IsValid)
        {
            return Invalid(validation.Violations);
        }

        var result = await _discovery.QueryAsync(
            validation.Filter!,
            Cursor,
            cancellationToken);
        if (!result.IsValid)
        {
            return Invalid(result.Violations);
        }

        Games = result.Page!.Items;
        NextPageUrl = BuildPageUrl(result.Page.NextCursor);
        if (_mediaStorageOptions.Enabled && Games.Count > 0)
        {
            Covers = await HttpContext.RequestServices
                .GetRequiredService<GameMediaQueryService>()
                .GetPublicCoversAsync(
                    Games.Select(game => game.Id).ToArray(),
                    cancellationToken);
        }

        return Page();
    }

    private PageResult Invalid(IReadOnlyList<ValidationViolation> violations)
    {
        Violations = violations;
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }

    private string? BuildPageUrl(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        var parameters = new Dictionary<string, StringValues>
        {
            ["cursor"] = cursor,
        };
        AddIfPresent(parameters, "q", Query);
        AddIfPresent(parameters, "genre", GenreCode);
        AddIfPresent(parameters, "createdFrom", CreatedFrom);
        AddIfPresent(parameters, "createdBefore", CreatedBefore);
        if (TagCodes.Length > 0)
        {
            parameters["tags"] = new StringValues(TagCodes);
        }

        if (PlatformCodes.Length > 0)
        {
            parameters["platforms"] = new StringValues(PlatformCodes);
        }

        var path = Request.PathBase.Add(Request.Path).Value ?? "/";
        return QueryHelpers.AddQueryString(path, parameters);
    }

    private static void AddIfPresent(
        IDictionary<string, StringValues> parameters,
        string name,
        string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            parameters[name] = value;
        }
    }
}
