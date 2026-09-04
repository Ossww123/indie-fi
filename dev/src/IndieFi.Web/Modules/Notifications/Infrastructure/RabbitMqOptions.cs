namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class RabbitMqOptions
{
    public const string SectionName = "Messaging:RabbitMq";

    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string EndpointPrefix { get; set; } = "indiefi";

    public static bool IsValid(RabbitMqOptions options) =>
        !options.Enabled
        || (IsSimpleValue(options.Host, 253)
            && options.Port > 0
            && IsSimpleValue(options.VirtualHost, 255)
            && IsSimpleValue(options.Username, 128)
            && !string.Equals(options.Username, "guest", StringComparison.OrdinalIgnoreCase)
            && IsSimpleValue(options.Password, 256)
            && IsEndpointPrefix(options.EndpointPrefix));

    private static bool IsSimpleValue(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && !value.Any(char.IsControl);

    private static bool IsEndpointPrefix(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 50
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_');
}
