namespace IndieFi.Web.Modules.KeyDistributions.Domain;

public enum GameKeyInputError
{
    Empty,
    TooLong,
    ContainsControlCharacter,
    Duplicate,
}

public sealed record GameKeyInputViolation(
    GameKeyInputError Error,
    int? LineNumber = null);

public sealed record GameKeyValue
{
    internal GameKeyValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > GameKeyPolicy.MaxLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("The game key value is invalid.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => "[REDACTED GAME KEY]";
}

public sealed record GameKeyBatchParseResult(
    IReadOnlyList<GameKeyValue> Keys,
    IReadOnlyList<GameKeyInputViolation> Violations)
{
    public bool IsValid => Violations.Count == 0;
}

public static class GameKeyPolicy
{
    public const int MaxLength = 200;

    public static GameKeyBatchParseResult ParseBatch(string? input)
    {
        var keys = new List<GameKeyValue>();
        var violations = new List<GameKeyInputViolation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasNonBlankLine = false;
        var lines = (input ?? string.Empty).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var value = lines[index].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            hasNonBlankLine = true;
            var lineNumber = index + 1;
            if (value.Length > MaxLength)
            {
                violations.Add(new GameKeyInputViolation(
                    GameKeyInputError.TooLong,
                    lineNumber));
                continue;
            }

            if (value.Any(char.IsControl))
            {
                violations.Add(new GameKeyInputViolation(
                    GameKeyInputError.ContainsControlCharacter,
                    lineNumber));
                continue;
            }

            if (!seen.Add(value))
            {
                violations.Add(new GameKeyInputViolation(
                    GameKeyInputError.Duplicate,
                    lineNumber));
                continue;
            }

            keys.Add(new GameKeyValue(value));
        }

        if (!hasNonBlankLine)
        {
            violations.Add(new GameKeyInputViolation(GameKeyInputError.Empty));
        }

        return new GameKeyBatchParseResult(keys, violations);
    }
}
