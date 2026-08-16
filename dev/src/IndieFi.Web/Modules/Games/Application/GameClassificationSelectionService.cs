using IndieFi.Web.Application.Validation;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Games.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Games.Application;

public sealed record GameClassificationInput(
    string? GenreCode,
    IReadOnlyList<string>? TagCodes,
    IReadOnlyList<string>? PlatformCodes);

public sealed record CurrentGameClassification(
    long GenreId,
    IReadOnlyList<long> TagIds,
    IReadOnlyList<long> PlatformIds);

public sealed record ValidatedGameClassification(
    long GenreId,
    IReadOnlyList<long> TagIds,
    IReadOnlyList<long> PlatformIds);

public sealed record GameClassificationValidationResult(
    ValidatedGameClassification? Selection,
    IReadOnlyList<ValidationViolation> Violations)
{
    public bool IsValid => Selection is not null;
}

public sealed class GameClassificationSelectionService
{
    public const string GenreField = "GenreCode";
    public const string TagsField = "TagCodes";
    public const string PlatformsField = "PlatformCodes";

    private readonly AppDbContext _db;

    public GameClassificationSelectionService(AppDbContext db)
    {
        _db = db;
    }

    public Task<GameClassificationValidationResult> ValidateForCreateAsync(
        GameClassificationInput input,
        CancellationToken cancellationToken = default)
    {
        return ValidateAsync(input, current: null, cancellationToken);
    }

    public Task<GameClassificationValidationResult> ValidateForUpdateAsync(
        GameClassificationInput input,
        CurrentGameClassification current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return ValidateAsync(input, current, cancellationToken);
    }

    private async Task<GameClassificationValidationResult> ValidateAsync(
        GameClassificationInput input,
        CurrentGameClassification? current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var violations = new List<ValidationViolation>();
        var tagCodes = input.TagCodes?.ToArray() ?? [];
        var platformCodes = input.PlatformCodes?.ToArray() ?? [];
        var allowedInactiveGenreId = current?.GenreId;

        var genreCanResolve = ValidateGenreShape(input.GenreCode, violations);
        var tagsCanResolve = ValidateCollectionShape(
            tagCodes,
            GameClassificationPolicy.MaxTagsPerGame,
            TagsField,
            "태그는 최대 8개까지 선택할 수 있습니다.",
            "같은 태그를 중복해서 선택할 수 없습니다.",
            "선택할 수 없는 태그가 포함되어 있습니다.",
            violations);
        var platformsCanResolve = ValidateCollectionShape(
            platformCodes,
            GameClassificationPolicy.MaxPlatformsPerGame,
            PlatformsField,
            "지원 플랫폼은 최대 12개까지 선택할 수 있습니다.",
            "같은 지원 플랫폼을 중복해서 선택할 수 없습니다.",
            "선택할 수 없는 지원 플랫폼이 포함되어 있습니다.",
            violations);
        if (platformCodes.Length == 0)
        {
            violations.Add(new ValidationViolation(
                PlatformsField,
                "지원 플랫폼을 하나 이상 선택해 주세요."));
            platformsCanResolve = false;
        }

        long? genreId = null;
        IReadOnlyList<long>? tagIds = null;
        IReadOnlyList<long>? platformIds = null;

        if (genreCanResolve)
        {
            genreId = await _db.GameGenres
                .AsNoTracking()
                .Where(item => item.Code == input.GenreCode
                    && (item.IsActive || item.Id == allowedInactiveGenreId))
                .Select(item => (long?)item.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (genreId is null)
            {
                violations.Add(new ValidationViolation(
                    GenreField,
                    "선택할 수 없는 장르입니다."));
            }
        }

        if (tagsCanResolve)
        {
            tagIds = await ResolveTagsAsync(
                tagCodes,
                current?.TagIds ?? [],
                cancellationToken);
            if (tagIds.Count != tagCodes.Length)
            {
                violations.Add(new ValidationViolation(
                    TagsField,
                    "선택할 수 없는 태그가 포함되어 있습니다."));
            }
        }

        if (platformsCanResolve)
        {
            platformIds = await ResolvePlatformsAsync(
                platformCodes,
                current?.PlatformIds ?? [],
                cancellationToken);
            if (platformIds.Count != platformCodes.Length)
            {
                violations.Add(new ValidationViolation(
                    PlatformsField,
                    "선택할 수 없는 지원 플랫폼이 포함되어 있습니다."));
            }
        }

        if (violations.Count > 0
            || genreId is null
            || tagIds is null
            || platformIds is null)
        {
            return new GameClassificationValidationResult(null, violations);
        }

        return new GameClassificationValidationResult(
            new ValidatedGameClassification(
                genreId.Value,
                tagIds,
                platformIds),
            []);
    }

    private Task<List<long>> ResolveTagsAsync(
        IReadOnlyList<string> codes,
        IReadOnlyList<long> allowedInactiveIds,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return Task.FromResult(new List<long>());
        }

        return _db.GameTags
            .AsNoTracking()
            .Where(item => codes.Contains(item.Code)
                && (item.IsActive || allowedInactiveIds.Contains(item.Id)))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    private Task<List<long>> ResolvePlatformsAsync(
        IReadOnlyList<string> codes,
        IReadOnlyList<long> allowedInactiveIds,
        CancellationToken cancellationToken)
    {
        return _db.GamePlatforms
            .AsNoTracking()
            .Where(item => codes.Contains(item.Code)
                && (item.IsActive || allowedInactiveIds.Contains(item.Id)))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    private static bool ValidateGenreShape(
        string? code,
        ICollection<ValidationViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            violations.Add(new ValidationViolation(
                GenreField,
                "장르를 선택해 주세요."));
            return false;
        }

        if (!GameClassificationPolicy.IsValidCode(code))
        {
            violations.Add(new ValidationViolation(
                GenreField,
                "선택할 수 없는 장르입니다."));
            return false;
        }

        return true;
    }

    private static bool ValidateCollectionShape(
        IReadOnlyList<string> codes,
        int maximumCount,
        string field,
        string maximumMessage,
        string duplicateMessage,
        string unavailableMessage,
        ICollection<ValidationViolation> violations)
    {
        var isValid = true;
        if (codes.Count > maximumCount)
        {
            violations.Add(new ValidationViolation(field, maximumMessage));
            isValid = false;
        }

        if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
        {
            violations.Add(new ValidationViolation(field, duplicateMessage));
            isValid = false;
        }

        if (codes.Any(code => !GameClassificationPolicy.IsValidCode(code)))
        {
            violations.Add(new ValidationViolation(field, unavailableMessage));
            isValid = false;
        }

        return isValid;
    }
}
