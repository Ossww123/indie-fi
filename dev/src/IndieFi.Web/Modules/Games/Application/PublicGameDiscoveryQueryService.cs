using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Games.Application;

public sealed record PublicGameDiscoveryItem(
    long Id,
    string Title,
    string? Description,
    DateTimeOffset CreatedAt);

public sealed record PublicGameDiscoveryPage(
    IReadOnlyList<PublicGameDiscoveryItem> Items,
    string? NextCursor);

public sealed record PublicGameDiscoveryQueryResult(
    PublicGameDiscoveryPage? Page,
    IReadOnlyList<ValidationViolation> Violations,
    PublicGameDiscoveryCursorDecodeStatus? CursorFailure)
{
    public bool IsValid => Page is not null;
}

public sealed class PublicGameDiscoveryQueryService
{
    public const string CursorField = "Cursor";
    public const int PageSize = 24;

    private readonly AppDbContext _db;
    private readonly PublicGameDiscoveryCursorCodec _cursorCodec;

    public PublicGameDiscoveryQueryService(
        AppDbContext db,
        PublicGameDiscoveryCursorCodec cursorCodec)
    {
        _db = db;
        _cursorCodec = cursorCodec;
    }

    public async Task<PublicGameDiscoveryQueryResult> QueryAsync(
        PublicGameDiscoveryFilter filter,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var catalogViolations = await ValidateCatalogAsync(
            filter,
            cancellationToken);
        if (catalogViolations.Count > 0)
        {
            return Invalid(catalogViolations);
        }

        PublicGameDiscoveryCursorBoundary? boundary = null;
        if (cursor is not null)
        {
            var decoded = _cursorCodec.Decode(cursor, filter);
            if (decoded.Status != PublicGameDiscoveryCursorDecodeStatus.Success)
            {
                return InvalidCursor(decoded.Status);
            }

            boundary = decoded.Boundary;
        }

        var games = _db.Games.AsNoTracking().AsQueryable();
        foreach (var token in filter.SearchTokens)
        {
            var pattern = $"%{EscapeLike(token.ToLowerInvariant())}%";
            games = games.Where(game => EF.Functions.Like(
                (game.Title + " " + (game.Description ?? string.Empty)).ToLower(),
                pattern,
                "\\"));
        }

        if (filter.GenreCode is not null)
        {
            games = games.Where(game => _db.GameGenres.Any(genre =>
                genre.Id == game.GenreId
                && genre.IsActive
                && genre.Code == filter.GenreCode));
        }

        foreach (var tagCode in filter.TagCodes)
        {
            var requiredTagCode = tagCode;
            games = games.Where(game =>
                (from assignment in _db.GameTagAssignments
                 join tag in _db.GameTags on assignment.TagId equals tag.Id
                 where assignment.GameId == game.Id
                     && tag.IsActive
                     && tag.Code == requiredTagCode
                 select assignment).Any());
        }

        if (filter.PlatformCodes.Count > 0)
        {
            games = games.Where(game =>
                (from assignment in _db.GamePlatformAssignments
                 join platform in _db.GamePlatforms
                     on assignment.PlatformId equals platform.Id
                 where assignment.GameId == game.Id
                     && platform.IsActive
                     && filter.PlatformCodes.Contains(platform.Code)
                 select assignment).Any());
        }

        if (filter.CreatedFromUtc.HasValue)
        {
            games = games.Where(game =>
                game.CreatedAt >= filter.CreatedFromUtc.Value);
        }

        if (filter.CreatedBeforeUtc.HasValue)
        {
            games = games.Where(game =>
                game.CreatedAt < filter.CreatedBeforeUtc.Value);
        }

        if (boundary is not null)
        {
            games = games.Where(game =>
                game.CreatedAt < boundary.CreatedAt
                || (game.CreatedAt == boundary.CreatedAt
                    && game.Id < boundary.GameId));
        }

        var items = await games
            .OrderByDescending(game => game.CreatedAt)
            .ThenByDescending(game => game.Id)
            .Take(PageSize + 1)
            .Select(game => new PublicGameDiscoveryItem(
                game.Id,
                game.Title,
                game.Description,
                game.CreatedAt))
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > PageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        var last = hasMore ? items[^1] : null;
        var nextCursor = last is null
            ? null
            : _cursorCodec.Encode(
                new PublicGameDiscoveryCursorBoundary(
                    last.CreatedAt,
                    last.Id),
                filter);
        return new PublicGameDiscoveryQueryResult(
            new PublicGameDiscoveryPage(items, nextCursor),
            [],
            null);
    }

    private async Task<List<ValidationViolation>> ValidateCatalogAsync(
        PublicGameDiscoveryFilter filter,
        CancellationToken cancellationToken)
    {
        var violations = new List<ValidationViolation>();
        if (filter.GenreCode is not null
            && !await _db.GameGenres.AsNoTracking().AnyAsync(
                genre => genre.IsActive && genre.Code == filter.GenreCode,
                cancellationToken))
        {
            violations.Add(new ValidationViolation(
                PublicGameDiscoveryInputValidator.GenreField,
                "활성 상태인 장르 코드만 선택할 수 있습니다."));
        }

        if (filter.TagCodes.Count > 0)
        {
            var activeTagCount = await _db.GameTags.AsNoTracking().CountAsync(
                tag => tag.IsActive && filter.TagCodes.Contains(tag.Code),
                cancellationToken);
            if (activeTagCount != filter.TagCodes.Count)
            {
                violations.Add(new ValidationViolation(
                    PublicGameDiscoveryInputValidator.TagsField,
                    "알 수 없거나 비활성인 태그 코드가 포함되어 있습니다."));
            }
        }

        if (filter.PlatformCodes.Count > 0)
        {
            var activePlatformCount = await _db.GamePlatforms.AsNoTracking()
                .CountAsync(
                    platform => platform.IsActive
                        && filter.PlatformCodes.Contains(platform.Code),
                    cancellationToken);
            if (activePlatformCount != filter.PlatformCodes.Count)
            {
                violations.Add(new ValidationViolation(
                    PublicGameDiscoveryInputValidator.PlatformsField,
                    "알 수 없거나 비활성인 지원 플랫폼 코드가 포함되어 있습니다."));
            }
        }

        return violations;
    }

    private static string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static PublicGameDiscoveryQueryResult Invalid(
        IReadOnlyList<ValidationViolation> violations) =>
        new(null, violations, null);

    private static PublicGameDiscoveryQueryResult InvalidCursor(
        PublicGameDiscoveryCursorDecodeStatus status)
    {
        var message = status switch
        {
            PublicGameDiscoveryCursorDecodeStatus.Expired =>
                "검색 페이지 커서가 만료되었습니다. 첫 페이지부터 다시 조회해 주세요.",
            PublicGameDiscoveryCursorDecodeStatus.FilterMismatch =>
                "현재 검색 조건과 일치하지 않는 페이지 커서입니다.",
            _ => "검색 페이지 커서가 올바르지 않습니다.",
        };
        return new PublicGameDiscoveryQueryResult(
            null,
            [new ValidationViolation(CursorField, message)],
            status);
    }
}
