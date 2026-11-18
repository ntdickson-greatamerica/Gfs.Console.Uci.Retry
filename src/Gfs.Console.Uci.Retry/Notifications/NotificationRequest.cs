namespace Gfs.Console.Uci.Retry.Notifications;

/// <summary>The only optional attachment ever sent: the original error file, unchanged.</summary>
public sealed record EmailAttachment(string FileName, byte[] Content);

/// <summary>A fully built message ready for delivery.</summary>
public sealed record EmailMessage(string From, IReadOnlyList<string> To, string Subject, string Body, EmailAttachment? Attachment);

/// <summary>What to tell whom, decided from a processing result before any delivery is attempted.</summary>
public sealed record NotificationRequest(NotificationType Type, IReadOnlyList<string> Recipients, string Subject, string Body, EmailAttachment? Attachment)
{
    /// <summary>
    /// Business notifications are the only record that a person must act, so failing to deliver them is itself a failure.
    /// Technical notifications are best effort; AA's generic fallback alert covers the rest.
    /// </summary>
    public bool IsBusinessCritical => Type != NotificationType.Technical;
}
