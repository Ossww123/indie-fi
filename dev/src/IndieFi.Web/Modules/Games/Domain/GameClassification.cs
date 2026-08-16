namespace IndieFi.Web.Modules.Games.Domain;

public sealed class GameGenre
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public sealed class GameTag
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public sealed class GamePlatform
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public sealed class GameTagAssignment
{
    public long GameId { get; set; }
    public long TagId { get; set; }
}

public sealed class GamePlatformAssignment
{
    public long GameId { get; set; }
    public long PlatformId { get; set; }
}
