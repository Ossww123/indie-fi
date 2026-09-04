namespace IndieFi.Web.Modules.Profiles.Domain;

public sealed class FavoriteGame
{
    public long UserId { get; set; }
    public long GameId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
