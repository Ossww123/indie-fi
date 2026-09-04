using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class NotificationOperationsOptions
{
    public const string SectionName = "NotificationOperations";

    public bool Enabled { get; set; } = true;
    public TimeSpan ObservationInterval { get; set; } = TimeSpan.FromSeconds(30);

    public static ValidateOptionsResult Validate(NotificationOperationsOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        return options.ObservationInterval >= TimeSpan.FromSeconds(5)
            && options.ObservationInterval <= TimeSpan.FromMinutes(10)
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    "Notification operations settings are invalid.");
    }
}

public sealed class NotificationOperationsOptionsValidator :
    IValidateOptions<NotificationOperationsOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        NotificationOperationsOptions options) =>
        NotificationOperationsOptions.Validate(options);
}
