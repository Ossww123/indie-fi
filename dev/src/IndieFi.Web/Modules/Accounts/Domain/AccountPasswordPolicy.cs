namespace IndieFi.Web.Modules.Accounts.Domain;

public enum AccountPasswordError
{
    None,
    Required,
    TooShort,
    TooLong,
}

/// <summary>
/// Defines the password length accepted by every account entry point.
/// Passwords are never trimmed because leading and trailing characters are significant.
/// </summary>
public static class AccountPasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static bool TryValidate(
        string? password,
        out AccountPasswordError error)
    {
        if (string.IsNullOrEmpty(password))
        {
            error = AccountPasswordError.Required;
            return false;
        }

        if (password.Length < MinLength)
        {
            error = AccountPasswordError.TooShort;
            return false;
        }

        if (password.Length > MaxLength)
        {
            error = AccountPasswordError.TooLong;
            return false;
        }

        error = AccountPasswordError.None;
        return true;
    }
}
