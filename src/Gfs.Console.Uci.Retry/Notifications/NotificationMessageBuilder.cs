using System.Text;
using Gfs.Console.Uci.Retry.Models;

namespace Gfs.Console.Uci.Retry.Notifications;

/// <summary>
/// Turns a finished <see cref="ProcessingResult"/> into a notification (or none): recipient selection, subject, body and the
/// optional original-error attachment. It performs no delivery.
/// </summary>
/// <remarks>
/// Messages use only an allow-list of non-sensitive facts: file name (no directories), outcome, error classification,
/// lifecycle flags, a UTC timestamp and the processor's own non-sensitive reason text. They never contain UCI content,
/// ZIP values, customer data or error-file content; the UCI identifier appears only when
/// <see cref="NotificationOptions.IncludeUciIdentifier"/> is enabled.
/// </remarks>
public sealed class NotificationMessageBuilder(NotificationOptions options)
{
    private enum AttachmentState { NotApplicable, Included, Unavailable, TooLarge }

    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Returns null when no notification is wanted (a successful automatic retry).</summary>
    public NotificationRequest? Build(ProcessingResult result)
    {
        if (result.Outcome == ProcessingOutcome.AutoRetrySubmitted)
        {
            return null;
        }

        var type = result.Outcome switch
        {
            ProcessingOutcome.ManualReview => NotificationType.ManualReview,
            ProcessingOutcome.AlreadyRetried => NotificationType.AlreadyRetried,
            _ => NotificationType.Technical
        };

        var recipients = (type == NotificationType.Technical ? options.TechnicalRecipients : options.ManualReviewRecipients)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var attachmentState = AttachmentState.NotApplicable;
        EmailAttachment? attachment = null;
        if (type != NotificationType.Technical)
        {
            attachment = TryAttach(result.OriginalErrorFile, out attachmentState);
        }

        var fileName = SafeFileName(result.OriginalErrorFile);
        return new NotificationRequest(type, recipients, Subject(result, type, fileName), Body(result, type, fileName, attachmentState), attachment);
    }

    private string Subject(ProcessingResult result, NotificationType type, string? fileName)
    {
        var label = type switch
        {
            NotificationType.ManualReview => "manual review required",
            NotificationType.AlreadyRetried => "automatic retry already used, manual review required",
            _ => result.Outcome switch
            {
                ProcessingOutcome.RemediationFailed => "retry processing failed",
                ProcessingOutcome.InvalidInput => "invalid input",
                _ => "processing error"
            }
        };

        var prefix = string.IsNullOrWhiteSpace(options.SubjectPrefix) ? "" : Clean(options.SubjectPrefix.Trim()) + " ";
        return $"{prefix}UCI {label}" + (fileName is null ? "" : $": {fileName}");
    }

    private string Body(ProcessingResult result, NotificationType type, string? fileName, AttachmentState attachment)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Outcome: {result.Outcome}");
        if (type == NotificationType.Technical)
        {
            sb.AppendLine($"Process exit code: {result.ExitCode}");
        }
        sb.AppendLine($"Error file: {fileName ?? "(not available)"}");
        if (result.ErrorType is { } errorType)
        {
            sb.AppendLine($"Error classification: {errorType}");
        }
        if (options.IncludeUciIdentifier && !string.IsNullOrWhiteSpace(result.UciIdentifier))
        {
            sb.AppendLine($"UCI identifier: {Clean(result.UciIdentifier)}");
        }
        sb.AppendLine($"Processed (UTC): {UtcNow():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Retry marker claimed: {YesNo(result.RetryClaimed)}");
        sb.AppendLine($"Corrected UCI moved outbound: {YesNo(result.OutboundReleased)}");
        sb.AppendLine(type == NotificationType.Technical
            ? $"Original error file archived: {YesNo(result.ErrorArchived)}"
            : "Original error file: archived after this notification is sent");
        if (result.ArchivedErrorFile is not null)
        {
            sb.AppendLine($"Archived as: {SafeFileName(result.ArchivedErrorFile)}");
        }
        sb.AppendLine();
        sb.AppendLine($"Detail: {Clean(result.Message)}");
        sb.AppendLine();
        sb.AppendLine(Explanation(result, type));
        sb.AppendLine();
        sb.AppendLine($"Next action: {NextAction(result, type)}");

        switch (attachment)
        {
            case AttachmentState.Included:
                sb.AppendLine().AppendLine("The original error file is attached.");
                break;
            case AttachmentState.TooLarge:
                sb.AppendLine().AppendLine("The original error file was not attached because it exceeds the configured size limit.");
                break;
            case AttachmentState.Unavailable:
                sb.AppendLine().AppendLine("The original error file could not be attached because it was not available.");
                break;
        }

        return sb.ToString();
    }

    private static string Explanation(ProcessingResult result, NotificationType type) => type switch
    {
        NotificationType.ManualReview =>
            "This error cannot be corrected automatically. No retry was attempted. The original error file is attached and will be moved to the archive once this notification is sent.",
        NotificationType.AlreadyRetried =>
            "An automatic retry marker already exists for this error file. No new retry was attempted, no corrected UCI was released, "
            + "and another automatic retry is prohibited. The original error file will be moved to the archive once this notification is sent.",
        _ => result.Outcome switch
        {
            ProcessingOutcome.RemediationFailed => StageText(result),
            ProcessingOutcome.InvalidInput => "The application was invoked with an invalid or missing error-file argument. No file was changed.",
            _ => "A technical failure prevented the application from completing the required disposition. See the application log for details."
        }
    };

    private static string StageText(ProcessingResult result) => result.FailureStage switch
    {
        FailureStage.BeforeClaim =>
            "The failure occurred before the retry marker was claimed. Nothing was released outbound and the automatic retry has not been consumed. "
            + "The original error file was left in place.",
        FailureStage.AfterClaim =>
            "The failure occurred after the retry marker was claimed but before the corrected UCI was moved outbound. "
            + "The automatic retry is considered consumed and must not be attempted automatically again. Nothing was released outbound.",
        FailureStage.AfterOutboundMove =>
            "The failure occurred after the corrected UCI was moved outbound, so the downstream process may already have observed it. "
            + "The automatic retry is considered consumed and must not be attempted automatically again. Nothing was rolled back.",
        FailureStage.ArchiveFailed =>
            "The corrected UCI was moved outbound, but archiving the original error file failed. The downstream process may already have observed the outbound file. "
            + "The automatic retry is considered consumed and must not be attempted automatically again. Nothing was rolled back, "
            + "and the original error file may still be at its source.",
        _ => result.RetryClaimed
            ? "The failure occurred after the retry marker was claimed. The automatic retry is considered consumed and must not be attempted automatically again."
            : "The failure occurred before the retry marker was claimed."
    };

    private static string NextAction(ProcessingResult result, NotificationType type) => type switch
    {
        NotificationType.ManualReview => "Review the archived original error file and resolve the error manually.",
        NotificationType.AlreadyRetried => "Investigate manually; do not resubmit automatically.",
        _ => result.RetryClaimed || result.OutboundReleased
            ? "Investigate manually. Do not retry this error file automatically; check whether the outbound file was processed."
            : "Investigate the technical cause. Support should review the application log."
    };

    private EmailAttachment? TryAttach(string? path, out AttachmentState state)
    {
        state = AttachmentState.Unavailable;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }
            if (info.Length > options.MaxAttachmentBytes)
            {
                state = AttachmentState.TooLarge;
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            state = AttachmentState.Included;
            return new EmailAttachment(info.Name, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? SafeFileName(string? path)
    {
        var name = string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? null : Clean(name);
    }

    // Prevents header injection through values that end up in the subject.
    private static string Clean(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).Take(300).ToArray());

    private static string YesNo(bool value) => value ? "Yes" : "No";
}
