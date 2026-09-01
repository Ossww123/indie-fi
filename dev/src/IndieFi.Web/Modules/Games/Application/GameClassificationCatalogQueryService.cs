using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IndieFi.Web.Modules.Games.Application;

public sealed record GameClassificationOption(
    long Id,
    string Code,
    string DisplayName,
    bool IsActive);

public sealed record GameClassificationSelectionCatalog(
    IReadOnlyList<GameClassificationOption> Genres,
    IReadOnlyList<GameClassificationOption> Tags,
    IReadOnlyList<GameClassificationOption> Platforms);

public sealed record GameClassificationEditorCatalog(
    GameClassificationSelectionCatalog Options,
    long SelectedGenreId,
    IReadOnlyList<long> SelectedTagIds,
    IReadOnlyList<long> SelectedPlatformIds);

public sealed record GameClassificationDetailCatalog(
    GameClassificationOption Genre,
    IReadOnlyList<GameClassificationOption> Tags,
    IReadOnlyList<GameClassificationOption> Platforms);

public sealed class GameClassificationCatalogQueryService
{
    private static readonly object ActiveCatalogCacheKey = new();
    private static readonly SemaphoreSlim ActiveCatalogCacheGate = new(1, 1);
    private static readonly TimeSpan ActiveCatalogCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public GameClassificationCatalogQueryService(
        AppDbContext db,
        IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<GameClassificationSelectionCatalog> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(
                ActiveCatalogCacheKey,
                out GameClassificationSelectionCatalog? cached)
            && cached is not null)
        {
            return cached;
        }

        await ActiveCatalogCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(
                    ActiveCatalogCacheKey,
                    out cached)
                && cached is not null)
            {
                return cached;
            }

            var loaded = new GameClassificationSelectionCatalog(
                (await QueryGenresAsync(null, cancellationToken)).AsReadOnly(),
                (await QueryTagsAsync([], cancellationToken)).AsReadOnly(),
                (await QueryPlatformsAsync([], cancellationToken)).AsReadOnly());

            _cache.Set(
                ActiveCatalogCacheKey,
                loaded,
                ActiveCatalogCacheLifetime);
            return loaded;
        }
        finally
        {
            ActiveCatalogCacheGate.Release();
        }
    }

    public async Task<GameClassificationEditorCatalog?> GetForGameAsync(
        long gameId,
        CancellationToken cancellationToken = default)
    {
        if (gameId <= 0)
        {
            return null;
        }

        var genreId = await _db.Games
            .AsNoTracking()
            .Where(game => game.Id == gameId)
            .Select(game => (long?)game.GenreId)
            .SingleOrDefaultAsync(cancellationToken);
        if (genreId is null)
        {
            return null;
        }

        var tagIds = await (
                from assignment in _db.GameTagAssignments.AsNoTracking()
                join tag in _db.GameTags.AsNoTracking()
                    on assignment.TagId equals tag.Id
                where assignment.GameId == gameId
                orderby tag.SortOrder, tag.Code
                select tag.Id)
            .ToListAsync(cancellationToken);
        var platformIds = await (
                from assignment in _db.GamePlatformAssignments.AsNoTracking()
                join platform in _db.GamePlatforms.AsNoTracking()
                    on assignment.PlatformId equals platform.Id
                where assignment.GameId == gameId
                orderby platform.SortOrder, platform.Code
                select platform.Id)
            .ToListAsync(cancellationToken);

        var options = new GameClassificationSelectionCatalog(
            await QueryGenresAsync(genreId.Value, cancellationToken),
            await QueryTagsAsync(tagIds, cancellationToken),
            await QueryPlatformsAsync(platformIds, cancellationToken));

        return new GameClassificationEditorCatalog(
            options,
            genreId.Value,
            tagIds,
            platformIds);
    }

    public async Task<GameClassificationDetailCatalog?> GetDetailAsync(
        long gameId,
        CancellationToken cancellationToken = default)
    {
        if (gameId <= 0)
        {
            return null;
        }

        var genre = await (
                from game in _db.Games.AsNoTracking()
                join item in _db.GameGenres.AsNoTracking()
                    on game.GenreId equals item.Id
                where game.Id == gameId
                select new GameClassificationOption(
                    item.Id,
                    item.Code,
                    item.DisplayName,
                    item.IsActive))
            .SingleOrDefaultAsync(cancellationToken);
        if (genre is null)
        {
            return null;
        }

        var tags = await (
                from assignment in _db.GameTagAssignments.AsNoTracking()
                join item in _db.GameTags.AsNoTracking()
                    on assignment.TagId equals item.Id
                where assignment.GameId == gameId
                orderby item.SortOrder, item.Code
                select new GameClassificationOption(
                    item.Id,
                    item.Code,
                    item.DisplayName,
                    item.IsActive))
            .ToListAsync(cancellationToken);
        var platforms = await (
                from assignment in _db.GamePlatformAssignments.AsNoTracking()
                join item in _db.GamePlatforms.AsNoTracking()
                    on assignment.PlatformId equals item.Id
                where assignment.GameId == gameId
                orderby item.SortOrder, item.Code
                select new GameClassificationOption(
                    item.Id,
                    item.Code,
                    item.DisplayName,
                    item.IsActive))
            .ToListAsync(cancellationToken);

        return new GameClassificationDetailCatalog(genre, tags, platforms);
    }

    private Task<List<GameClassificationOption>> QueryGenresAsync(
        long? selectedGenreId,
        CancellationToken cancellationToken)
    {
        return _db.GameGenres
            .AsNoTracking()
            .Where(item => item.IsActive || item.Id == selectedGenreId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => new GameClassificationOption(
                item.Id,
                item.Code,
                item.DisplayName,
                item.IsActive))
            .ToListAsync(cancellationToken);
    }

    private Task<List<GameClassificationOption>> QueryTagsAsync(
        IReadOnlyList<long> selectedTagIds,
        CancellationToken cancellationToken)
    {
        return _db.GameTags
            .AsNoTracking()
            .Where(item => item.IsActive || selectedTagIds.Contains(item.Id))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => new GameClassificationOption(
                item.Id,
                item.Code,
                item.DisplayName,
                item.IsActive))
            .ToListAsync(cancellationToken);
    }

    private Task<List<GameClassificationOption>> QueryPlatformsAsync(
        IReadOnlyList<long> selectedPlatformIds,
        CancellationToken cancellationToken)
    {
        return _db.GamePlatforms
            .AsNoTracking()
            .Where(item => item.IsActive || selectedPlatformIds.Contains(item.Id))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => new GameClassificationOption(
                item.Id,
                item.Code,
                item.DisplayName,
                item.IsActive))
            .ToListAsync(cancellationToken);
    }
}
