namespace IndieFi.Web.Application.Validation;

/// <summary>
/// A single broken input rule. Services return every violation they find so a caller
/// can show all of them at once instead of revealing them one refresh at a time.
/// </summary>
/// <param name="Field">
/// Name of the input the rule belongs to. It matches the bound property name on the
/// page so a caller can attach the message to the right form field.
/// </param>
public sealed record ValidationViolation(string Field, string Message);
