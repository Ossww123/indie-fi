using IndieFi.Web.Modules.Games.Domain;

namespace IndieFi.Web.Entities;

public sealed class GamePlayLink
{
    public long Id { get; set; }
    public long GameId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public GamePlayLinkProvider Provider { get; set; }
    public GamePlayLinkPlatform? TargetPlatform { get; set; }
    public string Url { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public enum GamePlayLinkProvider
{
    Steam,
    ItchIo,
    DeveloperWebsite,
    Other,
}

public enum GamePlayLinkPlatform
{
    Windows,
    MacOS,
    Linux,
    Web,
    Android,
    Ios,
    Multiple,
    Other,
}
