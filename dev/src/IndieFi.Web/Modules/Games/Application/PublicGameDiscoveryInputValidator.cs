using System.Globalization;
using System.Text;
using IndieFi.Web.Application.Validation;
using IndieFi.Web.Modules.Games.Domain;

namespace IndieFi.Web.Modules.Games.Application;

public sealed record PublicGameDiscoveryInput(
    string? Query,
    string? GenreCode,
    IReadOnlyList<string>? TagCodes,
    IReadOnlyList<string>? PlatformCodes,
    string? CreatedFrom,
    string? CreatedBefore);

public sealed record PublicGameDiscoveryFilter(
    string? Query,
    IReadOnlyList<string> SearchTokens,
    string? GenreCode,
    IReadOnlyList<string> TagCodes,
    IReadOnlyList<string> PlatformCodes,
    DateTimeOffset? CreatedFromUtc,
    DateTimeOffset? CreatedBeforeUtc);

public sealed record PublicGameDiscoveryValidationResult(
    PublicGameDiscoveryFilter? Filter,
    IReadOnlyList<ValidationViolation> Violations)
{
    public bool IsValid => Filter is not null;
}

public static class PublicGameDiscoveryInputValidator
{
    public const string QueryField = "Query";
    public const string GenreField = "GenreCode";
    public const string TagsField = "TagCodes";
    public const string PlatformsField = "PlatformCodes";
    public const string CreatedFromField = "CreatedFrom";
    public const string CreatedBeforeField = "CreatedBefore";

    public const int QueryMinimumLength = 2;
    public const int QueryMaximumLength = 100;
    public const int MaximumSearchTokens = 8;

    public static PublicGameDiscoveryValidationResult Validate(
        PublicGameDiscoveryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var violations = new List<ValidationViolation>();
        var query = NormalizeQuery(input.Query, violations);
        var tokens = query?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var genreCode = NormalizeGenre(input.GenreCode, violations);
        var tagCodes = NormalizeCodes(
            input.TagCodes,
            GameClassificationPolicy.MaxTagsPerGame,
            TagsField,
            "태그는 최대 8개까지 선택할 수 있습니다.",
            "선택할 수 없는 태그 코드가 포함되어 있습니다.",
            violations);
        var platformCodes = NormalizeCodes(
            input.PlatformCodes,
            GameClassificationPolicy.MaxPlatformsPerGame,
            PlatformsField,
            "지원 플랫폼은 최대 12개까지 선택할 수 있습니다.",
            "선택할 수 없는 지원 플랫폼 코드가 포함되어 있습니다.",
            violations);
        var createdFrom = NormalizeTimestamp(
            input.CreatedFrom,
            CreatedFromField,
            "등록 시작 시각은 오프셋을 포함한 RFC 3339 형식이어야 합니다.",
            violations);
        var createdBefore = NormalizeTimestamp(
            input.CreatedBefore,
            CreatedBeforeField,
            "등록 종료 시각은 오프셋을 포함한 RFC 3339 형식이어야 합니다.",
            violations);

        if (createdFrom.HasValue
            && createdBefore.HasValue
            && createdFrom.Value >= createdBefore.Value)
        {
            violations.Add(new ValidationViolation(
                CreatedBeforeField,
                "등록 종료 시각은 시작 시각보다 뒤여야 합니다."));
        }

        if (violations.Count > 0)
        {
            return new PublicGameDiscoveryValidationResult(null, violations);
        }

        return new PublicGameDiscoveryValidationResult(
            new PublicGameDiscoveryFilter(
                query,
                tokens,
                genreCode,
                tagCodes,
                platformCodes,
                createdFrom,
                createdBefore),
            []);
    }

    private static string? NormalizeQuery(
        string? input,
        ICollection<ValidationViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var query = CollapseWhitespace(input);
        var runeCount = query.EnumerateRunes().Count();
        if (runeCount is < QueryMinimumLength or > QueryMaximumLength)
        {
            violations.Add(new ValidationViolation(
                QueryField,
                "검색어는 공백 정리 후 2~100자여야 합니다."));
        }

        var tokenCount = query.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries).Length;
        if (tokenCount > MaximumSearchTokens)
        {
            violations.Add(new ValidationViolation(
                QueryField,
                "검색어는 공백 기준 최대 8개 토큰까지 사용할 수 있습니다."));
        }

        return query;
    }

    private static string? NormalizeGenre(
        string? input,
        ICollection<ValidationViolation> violations)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        if (!GameClassificationPolicy.IsValidCode(input))
        {
            violations.Add(new ValidationViolation(
                GenreField,
                "선택할 수 없는 장르 코드입니다."));
            return null;
        }

        return input;
    }

    private static IReadOnlyList<string> NormalizeCodes(
        IReadOnlyList<string>? input,
        int maximumCount,
        string field,
        string maximumMessage,
        string invalidMessage,
        ICollection<ValidationViolation> violations)
    {
        if (input is null || input.Count == 0)
        {
            return [];
        }

        var codes = new List<string>(input.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var containsInvalidCode = false;
        foreach (var code in input)
        {
            if (!GameClassificationPolicy.IsValidCode(code))
            {
                containsInvalidCode = true;
                continue;
            }

            if (seen.Add(code))
            {
                codes.Add(code);
            }
        }

        if (codes.Count > maximumCount)
        {
            violations.Add(new ValidationViolation(field, maximumMessage));
        }

        if (containsInvalidCode)
        {
            violations.Add(new ValidationViolation(field, invalidMessage));
        }

        return codes;
    }

    private static DateTimeOffset? NormalizeTimestamp(
        string? input,
        string field,
        string message,
        ICollection<ValidationViolation> violations)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        if (!TryParseRfc3339(input, out var value))
        {
            violations.Add(new ValidationViolation(field, message));
            return null;
        }

        return value;
    }

    private static bool TryParseRfc3339(
        string input,
        out DateTimeOffset value)
    {
        value = default;
        if (input.Length < 20)
        {
            return false;
        }

        string dateTimePart;
        TimeSpan offset;
        if (input[^1] == 'Z')
        {
            dateTimePart = input[..^1];
            offset = TimeSpan.Zero;
        }
        else
        {
            if (input.Length < 25
                || input[^6] is not ('+' or '-')
                || input[^3] != ':'
                || !int.TryParse(
                    input.AsSpan(input.Length - 5, 2),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var offsetHours)
                || !int.TryParse(
                    input.AsSpan(input.Length - 2, 2),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var offsetMinutes)
                || offsetHours > 14
                || offsetMinutes > 59
                || (offsetHours == 14 && offsetMinutes != 0))
            {
                return false;
            }

            var direction = input[^6] == '-' ? -1 : 1;
            offset = new TimeSpan(
                direction * offsetHours,
                direction * offsetMinutes,
                0);
            dateTimePart = input[..^6];
        }

        if (!TryParseDateTimePart(dateTimePart, out var dateTime))
        {
            return false;
        }

        value = new DateTimeOffset(dateTime, offset).ToUniversalTime();
        return true;
    }

    private static bool TryParseDateTimePart(string input, out DateTime value)
    {
        value = default;
        if (input.Length < 19)
        {
            return false;
        }

        var format = "yyyy-MM-dd'T'HH:mm:ss";
        if (input.Length > 19)
        {
            var fractionLength = input.Length - 20;
            if (input[19] != '.'
                || fractionLength is < 1 or > 7
                || input.AsSpan(20).ContainsAnyExceptInRange('0', '9'))
            {
                return false;
            }

            format += "." + new string('f', fractionLength);
        }

        return DateTime.TryParseExact(
            input,
            format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }

    private static string CollapseWhitespace(string input)
    {
        var builder = new StringBuilder(input.Length);
        var pendingSpace = false;
        foreach (var rune in input.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }
}
