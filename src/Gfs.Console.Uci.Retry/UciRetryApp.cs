using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Notifications;
using Gfs.Console.Uci.Retry.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gfs.Console.Uci.Retry;

public static class UciRetryApp
{
    /// <summary>
    /// Runs one invocation. Writes exactly one ProcessingResult JSON object to <paramref name="stdout"/>;
    /// all logging goes to the supplied logger factory (stderr in production). Business email is sent here for ManualReview/AlreadyRetried and one technical email for any non-zero result, 
    /// so at most one of each per invocation; <paramref name="emailSender"/> is a test seam (SMTP when null).
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, ILoggerFactory loggerFactory, IConfiguration configuration, IEmailSender? emailSender = null)
    {
        var logger = loggerFactory.CreateLogger("UciRetry");
        ProcessingResult result;

        try
        {
            var errorFile = CommandLine.TryGetErrorFile(args, out var error);
            if (errorFile is null)
            {
                logger.LogWarning("Invalid input: {Reason}", error);
                result = new ProcessingResult { Outcome = ProcessingOutcome.InvalidInput, Message = error! };
            }
            else
            {
                logger.LogInformation("Processing started for {ErrorFile}.", Path.GetFileName(errorFile));
                var options = configuration.GetSection(ProcessorOptions.SectionName).Get<ProcessorOptions>() ?? new ProcessorOptions();
                result = Process(errorFile, options, logger);
                result = RequireArchiveBeforeNotifying(result, options, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure.");
            result = new ProcessingResult
            {
                Outcome = ProcessingOutcome.ProcessingError,
                Message = "An unexpected error occurred. See the application log for details."
            };
        }

        // Business notification (ManualReview/AlreadyRetried) first; a failure turns the result into a technical one.
        if (result.Outcome is ProcessingOutcome.ManualReview or ProcessingOutcome.AlreadyRetried)
        {
            result = Notify(result, configuration, logger, emailSender);
        }
        result = ArchiveIfHandledWithoutRetry(result, configuration, logger);

        // The one technical escalation: every non-zero result gets exactly one attempt. A failed attempt is logged by the
        // service and never escalated further, so the result (and exit code) stay as they were.
        if (result.ExitCode != 0)
        {
            result = NotifyTechnical(result, configuration, logger, emailSender);
        }

        // Everything needed for the notification has already happened; a failure to report must not change the result.
        try
        {
            logger.Log(result.ExitCode == 0 ? LogLevel.Information : LogLevel.Error, "Processing finished: outcome {Outcome}, exit code {ExitCode}.", result.Outcome, result.ExitCode);
            stdout.WriteLine(result.ToJson());
            stdout.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        return result.ExitCode;
    }

    // A handled-without-retry outcome must end with an archive move, so a missing ArchiveDirectory is caught before any business email is sent.
    private static ProcessingResult RequireArchiveBeforeNotifying(ProcessingResult result, ProcessorOptions options, ILogger logger)
    {
        if (result.Outcome is not (ProcessingOutcome.ManualReview or ProcessingOutcome.AlreadyRetried) || !string.IsNullOrWhiteSpace(options.ArchiveDirectory))
        {
            return result;
        }

        logger.LogError("Processor:ArchiveDirectory is not configured; the error file cannot be disposed of.");
        return result with
        {
            Outcome = ProcessingOutcome.ProcessingError,
            Message = "Processor:ArchiveDirectory is not configured, so the error file cannot be archived. Nothing was sent or moved."
        };
    }

    // ManualReview/AlreadyRetried reach this point only after their email was delivered. Exit 0 requires the original error file to be fully
    // disposed of, so it is archived here (same timestamped, non-overwriting move as after an auto-retry). A failure is a technical failure
    // (non-zero) and is escalated by the caller; the business email is never repeated.
    private static ProcessingResult ArchiveIfHandledWithoutRetry(ProcessingResult result, IConfiguration configuration, ILogger logger)
    {
        if (result.Outcome is not (ProcessingOutcome.ManualReview or ProcessingOutcome.AlreadyRetried) || result.OriginalErrorFile is null)
        {
            return result;
        }

        string failure;
        string? archivedPath = null;
        try
        {
            var options = configuration.GetSection(ProcessorOptions.SectionName).Get<ProcessorOptions>() ?? new ProcessorOptions();
            var archive = new UciSubmissionService(options, logger).ArchiveHandledError(result.OriginalErrorFile);
            if (archive.Success)
            {
                logger.LogInformation("Original error file archived after the {Outcome} notification.", result.Outcome);
                return result with { ArchivedErrorFile = archive.Path, ErrorArchived = true };
            }
            failure = archive.Failure!;
            archivedPath = archive.Path;
        }
        catch (Exception ex)
        {
            failure = $"The original error file could not be archived ({ex.GetType().Name}).";
        }

        logger.LogError("Archiving after the {Outcome} notification failed: {Reason}", result.Outcome, failure);
        return result with
        {
            Outcome = ProcessingOutcome.ProcessingError,
            ArchivedErrorFile = archivedPath,
            Message = $"The {result.Outcome} notification was delivered, but the original error file could not be archived: {failure} "
                + "Manual handling is required."
        };
    }

    /// <summary>Attempts the single technical notification for a failed invocation. Never throws and never recurses.</summary>
    internal static ProcessingResult NotifyTechnical(ProcessingResult result, IConfiguration configuration, ILogger logger, IEmailSender? emailSender = null)
    {
        logger.LogError("Technical failure ({Outcome}, exit code {ExitCode}); attempting the technical notification.", result.Outcome, result.ExitCode);
        return Notify(result, configuration, logger, emailSender);
    }

    // Delivers the notification for the result's outcome. A broken Notifications section is treated as "not configured" and never triggers another notification attempt.
    private static ProcessingResult Notify(ProcessingResult result, IConfiguration configuration, ILogger logger, IEmailSender? emailSender)
    {
        NotificationOptions options;
        try
        {
            options = configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>() ?? new NotificationOptions();
        }
        catch (Exception ex)
        {
            logger.LogError("Notification configuration could not be read ({Error}).", ex.GetType().Name);
            options = new NotificationOptions();
        }

        try
        {
            return new NotificationService(options, emailSender ?? new SmtpEmailSender(options), logger).Notify(result);
        }
        catch (Exception ex)
        {
            logger.LogError("Notification handling failed unexpectedly ({Error}).", ex.GetType().Name);
            return NotificationService.AsUndelivered(result, "notification handling failed unexpectedly");
        }
    }

    private static ProcessingResult Process(string errorFile, ProcessorOptions options, ILogger logger)
    {
        var processor = new UciErrorProcessor(options, logger);
        return processor.ToResult(processor.Analyze(errorFile), errorFile);
    }
}
