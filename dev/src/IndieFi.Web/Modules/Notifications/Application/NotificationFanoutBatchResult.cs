namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationFanoutBatchResult(
    int StagedCount,
    long? NextAfterUserId = null);
