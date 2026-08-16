namespace IndieFi.Web.Modules.Media.Application;

public static class MediaProcessingFailureCode
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        if (!IsLowercaseLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsLowercaseLetterOrDigit(character)
            || character is '_' or '.' or '-');
    }

    public static void RequireValid(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "The media processing failure code is invalid.",
                nameof(value));
        }
    }

    private static bool IsLowercaseLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
