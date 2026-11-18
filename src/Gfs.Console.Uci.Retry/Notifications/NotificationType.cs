namespace Gfs.Console.Uci.Retry.Notifications;

public enum NotificationType
{
    /// <summary>A business/data condition needs a person (ManualReview).</summary>
    ManualReview,

    /// <summary>The one automatic retry was already consumed (AlreadyRetried).</summary>
    AlreadyRetried,

    /// <summary>A technical failure (RemediationFailed, InvalidInput, ProcessingError).</summary>
    Technical
}
