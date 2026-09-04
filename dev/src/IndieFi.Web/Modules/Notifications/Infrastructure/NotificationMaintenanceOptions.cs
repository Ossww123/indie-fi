using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Notifications.Infrastructure;

public sealed class NotificationMaintenanceOptions
{
    public const string SectionName = "NotificationMaintenance";

    public bool Enabled { get; set; } = true;
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromHours(1);
    public int BatchSize { get; set; } = 500;

    public static ValidateOptionsResult Validate(
        NotificationMaintenanceOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        return options.WorkerInterval >= TimeSpan.FromMinutes(1)
            && options.WorkerInterval <= TimeSpan.FromDays(1)
            && options.BatchSize is >= 1 and <= 5000
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(
                    "Notification maintenance settings are invalid.");
    }
}

public sealed class NotificationMaintenanceOptionsValidator :
    IValidateOptions<NotificationMaintenanceOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        NotificationMaintenanceOptions options) =>
        NotificationMaintenanceOptions.Validate(options);
}
