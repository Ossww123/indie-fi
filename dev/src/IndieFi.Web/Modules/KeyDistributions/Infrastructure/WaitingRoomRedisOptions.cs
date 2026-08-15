namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public sealed class WaitingRoomRedisOptions
{
    public const string SectionName = "WaitingRoom:Redis";

    public bool Enabled { get; set; }

    public string ConnectionString { get; set; } = string.Empty;

    public string KeyPrefix { get; set; } = "indiefi:waiting-room:v1";

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public static bool IsValid(WaitingRoomRedisOptions options) =>
        !options.Enabled
        || (!string.IsNullOrWhiteSpace(options.ConnectionString)
            && IsValidPrefix(options.KeyPrefix)
            && options.ConnectTimeout > TimeSpan.Zero
            && options.ConnectTimeout <= TimeSpan.FromSeconds(30));

    private static bool IsValidPrefix(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is ':' or '-' or '_');
}
