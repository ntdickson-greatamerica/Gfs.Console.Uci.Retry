using Gfs.Console.Uci.Retry.Models;
using Microsoft.Extensions.Logging;

namespace Gfs.Console.Uci.Retry.Notifications;

/// <summary>
/// The single place that decides whether a notification is needed, delivers it, and adjusts the result when delivery fails.
/// Called once per invocation after the final processing result is known, so one invocation sends at most one notification.
/// </summary>
/// <remarks>
/// Delivery is bounded: at most <see cref="MaxAttempts"/> attempts with a fixed <see cref="RetryDelay"/> between them,
/// and only the exception type is logged (never recipients, bodies or identifiers).
/// <list type="bullet">
/// <item>No email for AutoRetrySubmitted.</item>
/// <item>ManualReview/AlreadyRetried must be delivered: if notifications are disabled, incomplete or delivery fails, the result becomes
/// ProcessingError (exit 50) and the original error file is untouched, so AA's generic alert and a person can pick it up.</item>
/// <item>Technical results keep their outcome. A RemediationFailed result after the retry claim additionally records that notification failed.</item>
/// </list>
/// </remarks>
public sealed class NotificationService(NotificationOptions options, IEmailSender sender, ILogger logger)
{
    public const int MaxAttempts = 2;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    internal Action<TimeSpan> Delay { get; set; } = Thread.Sleep;
    internal Func<DateTimeOffset>? UtcNow { get; set; }

    public ProcessingResult Notify(ProcessingResult result)
    {
        NotificationRequest? request;
        try
        {
            var builder = new NotificationMessageBuilder(options);
            if (UtcNow is not null)
            {
                builder.UtcNow = UtcNow;
            }
            request = builder.Build(result);
        }
        catch (Exception ex)
        {
            logger.LogError("Notification could not be built ({Error}).", ex.GetType().Name);
            return NotDelivered(result, null, "the notification could not be built");
        }

        if (request is null)
        {
            logger.LogInformation("No notification is required for outcome {Outcome}.", result.Outcome);
            return result;
        }

        var configurationProblem = ConfigurationProblem(request);
        if (configurationProblem is not null)
        {
            logger.LogWarning("Notification {Type} not sent: {Reason}.", request.Type, configurationProblem);
            return NotDelivered(result, request, configurationProblem);
        }

        var message = new EmailMessage(options.FromAddress.Trim(), request.Recipients, request.Subject, request.Body, request.Attachment);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                sender.Send(message);
                logger.LogInformation("Notification {Type} sent for outcome {Outcome}.", request.Type, result.Outcome);
                return result;
            }
            catch (Exception ex)
            {
                logger.LogError("Notification {Type} delivery attempt {Attempt} of {Max} failed ({Error}).", request.Type, attempt, MaxAttempts, ex.GetType().Name);
                if (attempt < MaxAttempts)
                {
                    Delay(RetryDelay);
                }
            }
        }

        return NotDelivered(result, request, "delivery failed");
    }

    /// <summary>Applies the undelivered-notification rules for a result whose notification could not be attempted at all (unexpected failure).</summary>
    public static ProcessingResult AsUndelivered(ProcessingResult result, string reason) => NotDelivered(result, null, reason);

    private string? ConfigurationProblem(NotificationRequest request)
    {
        if (!options.Enabled)
        {
            return "notifications are disabled";
        }
        if (string.IsNullOrWhiteSpace(options.SmtpHost) || options.SmtpPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(options.FromAddress))
        {
            return "notification settings are incomplete";
        }
        return request.Recipients.Count == 0 ? "no recipients are configured" : null;
    }

    internal static ProcessingResult NotDelivered(ProcessingResult result, NotificationRequest? request, string reason)
    {
        if (request is { IsBusinessCritical: true } || result.Outcome is ProcessingOutcome.ManualReview or ProcessingOutcome.AlreadyRetried)
        {
            return result with
            {
                Outcome = ProcessingOutcome.ProcessingError,
                Message = $"The {result.Outcome} notification could not be delivered ({reason}). "
                    + "The original error file was not archived or deleted and requires manual handling."
                    + (result.RetryClaimed ? " The automatic retry marker exists; no further automatic retry is allowed." : "")
            };
        }

        if (result.Outcome == ProcessingOutcome.RemediationFailed && result.RetryClaimed)
        {
            return result with
            {
                Message = result.Message + $" Notification delivery failed after the retry was claimed ({reason}); the retry remains consumed."
            };
        }

        return result;
    }
}
