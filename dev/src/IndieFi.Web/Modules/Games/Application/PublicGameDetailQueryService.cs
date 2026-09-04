using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Games.Application;

public sealed record PublicGameDetailGame(
    long Id,
    long OwnerId,
    string Title,
    string? Description,
    DateTimeOffset CreatedAt);

public sealed record PublicGamePlayLink(
    string DisplayName,
    GamePlayLinkProvider Provider,
    GamePlayLinkPlatform? TargetPlatform,
    string Url)
{
    public string ProviderText => Provider switch
    {
        GamePlayLinkProvider.Steam => "Steam",
        GamePlayLinkProvider.ItchIo => "itch.io",
        GamePlayLinkProvider.DeveloperWebsite => "개발자 사이트",
        _ => "기타",
    };

    public string? TargetPlatformText => TargetPlatform switch
    {
        GamePlayLinkPlatform.Windows => "Windows",
        GamePlayLinkPlatform.MacOS => "macOS",
        GamePlayLinkPlatform.Linux => "Linux",
        GamePlayLinkPlatform.Web => "Web",
        GamePlayLinkPlatform.Android => "Android",
        GamePlayLinkPlatform.Ios => "iOS",
        GamePlayLinkPlatform.Multiple => "복수 플랫폼",
        GamePlayLinkPlatform.Other => "기타 플랫폼",
        _ => null,
    };
}

public sealed record PublicGameDetail(
    PublicGameDetailGame Game,
    string OwnerDisplayName,
    GameClassificationDetailCatalog Classifications,
    IReadOnlyList<PublicGamePlayLink> PlayLinks,
    IReadOnlyList<PublicKeyDropDetail> Drops,
    int PendingRequestCount,
    bool IsFavorite);

public sealed class PublicGameDetailQueryService
{
    private const int TagRowKind = 1;
    private const int PlatformRowKind = 2;
    private const int PlayLinkRowKind = 3;

    private readonly AppDbContext _db;
    private readonly PublicKeyDropReader _keyDrops;

    public PublicGameDetailQueryService(
        AppDbContext db,
        PublicKeyDropReader keyDrops)
    {
        _db = db;
        _keyDrops = keyDrops;
    }

    public async Task<PublicGameDetail?> GetAsync(
        long gameId,
        long? viewerUserId,
        CancellationToken cancellationToken = default)
    {
        if (gameId <= 0)
        {
            return null;
        }

        if (viewerUserId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewerUserId));
        }

        var core = await (
                from game in _db.Games.AsNoTracking()
                join genre in _db.GameGenres.AsNoTracking()
                    on game.GenreId equals genre.Id
                join profile in _db.UserProfiles.AsNoTracking()
                    on game.OwnerId equals profile.UserId into profiles
                from profile in profiles.DefaultIfEmpty()
                where game.Id == gameId
                select new CoreRow(
                    game.Id,
                    game.OwnerId,
                    game.Title,
                    game.Description,
                    game.CreatedAt,
                    genre.Id,
                    genre.Code,
                    genre.DisplayName,
                    genre.IsActive,
                    profile == null ? null : profile.Nickname,
                    viewerUserId.HasValue
                        && viewerUserId.Value == game.OwnerId
                        ? 0
                        : _db.ReviewRequests.Count(request =>
                            request.GameId == game.Id
                            && request.Status == ReviewRequestStatus.Queued),
                    viewerUserId.HasValue
                        && _db.FavoriteGames.Any(favorite =>
                            favorite.UserId == viewerUserId.Value
                            && favorite.GameId == game.Id)))
            .SingleOrDefaultAsync(cancellationToken);
        if (core is null)
        {
            return null;
        }

        var relatedRows = await _db.Database
            .SqlQuery<RelatedRow>($$"""
                SELECT 1 AS "Kind",
                       tag.id AS "Id",
                       tag.code::text AS "Code",
                       tag.display_name::text AS "DisplayName",
                       tag.is_active AS "IsActive",
                       NULL::text AS "Provider",
                       NULL::text AS "TargetPlatform",
                       NULL::text AS "Url",
                       tag.sort_order AS "SortOrder"
                FROM game_tag_assignments AS assignment
                INNER JOIN game_tags AS tag ON tag.id = assignment.tag_id
                WHERE assignment.game_id = {{gameId}}

                UNION ALL

                SELECT 2 AS "Kind",
                       platform.id AS "Id",
                       platform.code::text AS "Code",
                       platform.display_name::text AS "DisplayName",
                       platform.is_active AS "IsActive",
                       NULL::text AS "Provider",
                       NULL::text AS "TargetPlatform",
                       NULL::text AS "Url",
                       platform.sort_order AS "SortOrder"
                FROM game_platform_assignments AS assignment
                INNER JOIN game_platforms AS platform
                    ON platform.id = assignment.platform_id
                WHERE assignment.game_id = {{gameId}}

                UNION ALL

                SELECT 3 AS "Kind",
                       link.id AS "Id",
                       NULL::text AS "Code",
                       link.display_name::text AS "DisplayName",
                       NULL::boolean AS "IsActive",
                       link.provider::text AS "Provider",
                       link.target_platform::text AS "TargetPlatform",
                       link.url::text AS "Url",
                       link.sort_order AS "SortOrder"
                FROM game_play_links AS link
                WHERE link.game_id = {{gameId}}

                ORDER BY "Kind", "SortOrder", "Code" NULLS LAST, "Id"
                """)
            .ToArrayAsync(cancellationToken);

        var tags = relatedRows
            .Where(row => row.Kind == TagRowKind)
            .Select(ToClassificationOption)
            .ToArray();
        var platforms = relatedRows
            .Where(row => row.Kind == PlatformRowKind)
            .Select(ToClassificationOption)
            .ToArray();
        var playLinks = relatedRows
            .Where(row => row.Kind == PlayLinkRowKind)
            .Select(row => new PublicGamePlayLink(
                row.DisplayName,
                Enum.Parse<GamePlayLinkProvider>(row.Provider!, ignoreCase: false),
                row.TargetPlatform is null
                    ? null
                    : Enum.Parse<GamePlayLinkPlatform>(
                        row.TargetPlatform,
                        ignoreCase: false),
                row.Url!))
            .ToArray();

        var drops = await _keyDrops.ListForGameAsync(gameId, cancellationToken);

        return new PublicGameDetail(
            new PublicGameDetailGame(
                core.GameId,
                core.OwnerId,
                core.Title,
                core.Description,
                core.CreatedAt),
            UserDisplayName.Resolve(core.OwnerId, core.OwnerNickname),
            new GameClassificationDetailCatalog(
                new GameClassificationOption(
                    core.GenreId,
                    core.GenreCode,
                    core.GenreDisplayName,
                    core.GenreIsActive),
                tags,
                platforms),
            playLinks,
            drops,
            core.PendingRequestCount,
            core.IsFavorite);
    }

    private static GameClassificationOption ToClassificationOption(RelatedRow row) =>
        new(row.Id, row.Code!, row.DisplayName, row.IsActive!.Value);

    private sealed record CoreRow(
        long GameId,
        long OwnerId,
        string Title,
        string? Description,
        DateTimeOffset CreatedAt,
        long GenreId,
        string GenreCode,
        string GenreDisplayName,
        bool GenreIsActive,
        string? OwnerNickname,
        int PendingRequestCount,
        bool IsFavorite);

    private sealed record RelatedRow
    {
        public int Kind { get; init; }

        public long Id { get; init; }

        public string? Code { get; init; }

        public string DisplayName { get; init; } = string.Empty;

        public bool? IsActive { get; init; }

        public string? Provider { get; init; }

        public string? TargetPlatform { get; init; }

        public string? Url { get; init; }

        public int SortOrder { get; init; }
    }
}
