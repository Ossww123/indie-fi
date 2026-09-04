namespace IndieFi.Web.Modules.Accounts.Domain;

public enum AccountEmailAddressError
{
    None,
    Required,
    TooLong,
    InvalidFormat,
}

public sealed record AccountEmailAddress(string DisplayValue, string NormalizedValue);

/// <summary>
/// Defines the email syntax accepted by every account entry point.
/// Ownership is proved separately by the email confirmation flow.
/// </summary>
public static class AccountEmailAddressPolicy
{
    public const int MaxLength = 254;
    public const int LocalPartMaxLength = 64;
    public const int DomainLabelMaxLength = 63;

    private const string AllowedLocalSymbols = "!#$%&'*+-/=?^_`{|}~";

    public static bool TryCreate(
        string? input,
        out AccountEmailAddress? address,
        out AccountEmailAddressError error)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = AccountEmailAddressError.Required;
            return false;
        }

        var displayValue = input.Trim();
        if (displayValue.Length > MaxLength)
        {
            error = AccountEmailAddressError.TooLong;
            return false;
        }

        var separatorIndex = displayValue.IndexOf('@');
        if (separatorIndex <= 0
            || separatorIndex != displayValue.LastIndexOf('@')
            || separatorIndex > LocalPartMaxLength)
        {
            error = AccountEmailAddressError.InvalidFormat;
            return false;
        }

        var localPart = displayValue.AsSpan(0, separatorIndex);
        var domain = displayValue.AsSpan(separatorIndex + 1);

        if (!IsValidLocalPart(localPart) || !IsValidDomain(domain))
        {
            error = AccountEmailAddressError.InvalidFormat;
            return false;
        }

        address = new AccountEmailAddress(
            displayValue,
            displayValue.ToUpperInvariant());
        error = AccountEmailAddressError.None;
        return true;
    }

    private static bool IsValidLocalPart(ReadOnlySpan<char> localPart)
    {
        if (localPart.IsEmpty
            || localPart[0] == '.'
            || localPart[^1] == '.')
        {
            return false;
        }

        var previousWasDot = false;
        foreach (var character in localPart)
        {
            if (character == '.')
            {
                if (previousWasDot)
                {
                    return false;
                }

                previousWasDot = true;
                continue;
            }

            if (!IsAsciiLetterOrDigit(character)
                && !AllowedLocalSymbols.Contains(character, StringComparison.Ordinal))
            {
                return false;
            }

            previousWasDot = false;
        }

        return true;
    }

    private static bool IsValidDomain(ReadOnlySpan<char> domain)
    {
        if (domain.IsEmpty || domain.IndexOf('.') < 0)
        {
            return false;
        }

        var labelStart = 0;
        for (var index = 0; index <= domain.Length; index++)
        {
            if (index < domain.Length && domain[index] != '.')
            {
                continue;
            }

            var label = domain[labelStart..index];
            if (label.IsEmpty
                || label.Length > DomainLabelMaxLength
                || !IsAsciiLetterOrDigit(label[0])
                || !IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }

            foreach (var character in label)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }

            labelStart = index + 1;
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';
    }
}
