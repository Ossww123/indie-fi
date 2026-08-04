namespace IndieFi.Web.Entities;

/// <summary>
/// A game registered by a user. One row per game registration.
/// </summary>
public class Game
{
    public long Id { get; set; }

    public long OwnerId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
