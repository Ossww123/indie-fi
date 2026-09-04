namespace IndieFi.Web.Modules.Profiles.Domain;

public sealed class UserFollow
{
    public long FollowerId { get; set; }
    public long FollowedUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
