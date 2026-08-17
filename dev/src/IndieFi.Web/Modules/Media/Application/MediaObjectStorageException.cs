namespace IndieFi.Web.Modules.Media.Application;

/// <summary>
/// Stable, provider-independent failure codes for media object operations.
/// Object keys, endpoints, credentials and signed URLs must not be placed in messages.
/// </summary>
public enum MediaObjectStorageFailureCode
{
    ObjectNotFound,
    PreconditionFailed,
    AccessDenied,
    TimedOut,
    Unavailable,
    InvalidResponse,
}

public sealed class MediaObjectStorageException : Exception
{
    public MediaObjectStorageException(
        MediaObjectStorageFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public MediaObjectStorageFailureCode FailureCode { get; }

    public bool IsTransient => FailureCode is
        MediaObjectStorageFailureCode.TimedOut or
        MediaObjectStorageFailureCode.Unavailable;
}
