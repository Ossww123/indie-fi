namespace IndieFi.Web.Modules.Profiles.Domain;

/// <summary>
/// Public information owned by one registered user. Accounts without a row, or
/// without a nickname, continue to use their stable fallback display identifier.
/// </summary>
public sealed class UserProfile
{
    public long UserId { get; set; }
    public string? Nickname { get; set; }
    public string? NormalizedNickname { get; set; }
    public string Biography { get; set; } = string.Empty;
    public DateTimeOffset? NicknameChangedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
