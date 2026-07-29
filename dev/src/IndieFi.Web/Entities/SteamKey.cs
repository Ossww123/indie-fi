namespace IndieFi.Web.Entities;

/// <summary>
/// A Steam key assigned to a scheduled drop. One row per key.
/// </summary>
public class SteamKey
{
    public long Id { get; set; }

    public long DropId { get; set; }

    public string KeyValue { get; set; } = string.Empty;

    public KeyDrop Drop { get; set; } = null!;

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
