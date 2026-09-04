using System.Text;

namespace IndieFi.Web.Modules.Profiles.Domain;

public enum NicknameValidationError
{
    None,
    Required,
    TooShort,
    TooLong,
    InvalidCharacter,
}

public enum BiographyValidationError
{
    None,
    TooLong,
}

public sealed record NicknameValue(string Value, string UniquenessKey);

public static class UserProfilePolicy
{
    public const int NicknameMinLength = 2;
    public const int NicknameMaxLength = 20;
    public const int BiographyMaxLength = 500;
    public static readonly TimeSpan NicknameChangeInterval = TimeSpan.FromDays(7);

    public static bool TryCreateNickname(
        string? input,
        out NicknameValue? nickname,
        out NicknameValidationError error)
    {
        nickname = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = NicknameValidationError.Required;
            return false;
        }

        var value = CollapseWhitespace(input.Normalize(NormalizationForm.FormKC));
        var length = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            length++;
            if (!IsAllowedNicknameRune(rune))
            {
                error = NicknameValidationError.InvalidCharacter;
                return false;
            }
        }

        if (length < NicknameMinLength)
        {
            error = NicknameValidationError.TooShort;
            return false;
        }

        if (length > NicknameMaxLength)
        {
            error = NicknameValidationError.TooLong;
            return false;
        }

        nickname = new NicknameValue(value, value.ToUpperInvariant());
        error = NicknameValidationError.None;
        return true;
    }

    public static bool CanChangeNickname(
        DateTimeOffset? lastChangedAt,
        DateTimeOffset now) =>
        lastChangedAt is null || now >= lastChangedAt.Value + NicknameChangeInterval;

    public static bool TryNormalizeBiography(
        string? input,
        out string biography,
        out BiographyValidationError error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            biography = string.Empty;
            error = BiographyValidationError.None;
            return true;
        }

        biography = input
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        var length = 0;
        foreach (var _ in biography.EnumerateRunes())
        {
            length++;
        }

        if (length > BiographyMaxLength)
        {
            error = BiographyValidationError.TooLong;
            return false;
        }

        error = BiographyValidationError.None;
        return true;
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

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static bool IsAllowedNicknameRune(Rune rune) =>
        rune.Value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or ' '
            or '_'
            or '-'
        || rune.Value is >= 0x1100 and <= 0x11ff
            or >= 0x3130 and <= 0x318f
            or >= 0xac00 and <= 0xd7a3;
}
