using IndieFi.Web.Modules.Games.Domain;

namespace IndieFi.Web.Entities;

/// <summary>
/// A game registered by a user. One row per game registration.
/// </summary>
public class Game
{
    public long Id { get; set; }

    public long OwnerId { get; set; }

    /// <summary>
    /// Existing games use the inactive legacy value until their owner selects an
    /// active genre. New registration starts requiring an active value in 10-3.
    /// </summary>
    public long GenreId { get; set; } = GameClassificationCatalog.UnspecifiedGenreId;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
