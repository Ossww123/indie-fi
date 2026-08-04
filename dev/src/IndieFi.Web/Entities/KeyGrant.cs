namespace IndieFi.Web.Entities;

/// <summary>
/// A Steam key granted to a user from a drop. One row per grant.
/// </summary>
public class KeyGrant
{
    public long Id { get; set; }

    public long DropId { get; set; }

    public long UserId { get; set; }

    public long KeyId { get; set; }

    /// <summary>
    /// Set by the database (<c>now()</c>), never by the application.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}
