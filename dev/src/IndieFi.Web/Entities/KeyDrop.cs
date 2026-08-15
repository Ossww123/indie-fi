namespace IndieFi.Web.Entities;

/// <summary>
/// A scheduled release of game keys. One row per drop.
/// </summary>
public class KeyDrop
{
    public long Id { get; set; }

    public long GameId { get; set; }

    public long OwnerId { get; set; }

    public DateTimeOffset OpensAt { get; set; }

    public int TotalKeys { get; set; }

    public int? MinimumAccountAgeDays { get; set; }

    public int? MinimumCompletedReviewCount { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public long StateVersion { get; set; }

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
