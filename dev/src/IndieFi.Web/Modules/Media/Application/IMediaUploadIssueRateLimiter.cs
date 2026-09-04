namespace IndieFi.Web.Modules.Media.Application;

public enum MediaUploadIssueRateLimitOutcome
{
    Allowed,
    ShortWindowLimitReached,
    DailyLimitReached,
}

public interface IMediaUploadIssueRateLimiter
{
    Task<MediaUploadIssueRateLimitOutcome> TryAcquireAsync(
        long ownerUserId,
        Guid issueId,
        CancellationToken cancellationToken = default);
}

public sealed class MediaUploadIssueRateLimitUnavailableException : Exception
{
    public MediaUploadIssueRateLimitUnavailableException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}
