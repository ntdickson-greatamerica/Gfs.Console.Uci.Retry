namespace Gfs.Console.Uci.Retry.Models;

/// <summary>
/// The application's internal disposition of one error file. It drives notification selection, logging and tests; it is NOT
/// the process exit code (see <see cref="ProcessingOutcomeExtensions.ToExitCode"/>).
/// </summary>
public enum ProcessingOutcome
{
    AutoRetrySubmitted,
    ManualReview,
    AlreadyRetried,
    RemediationFailed,
    InvalidInput,
    ProcessingError
}

public static class ProcessingOutcomeExtensions
{
    /// <summary>
    /// The single authoritative mapping from the FINAL outcome to the process exit code, which is AA's only contract:
    /// 0 = the application handled the error file; non-zero = it did not and a technical alert is needed.
    /// </summary>
    /// <remarks>
    /// ManualReview and AlreadyRetried are handled outcomes only because they reach this point after their business
    /// notification was delivered and the original error file was archived: an undelivered notification or failed archive is converted to ProcessingError first.
    /// The distinct non-zero codes exist for troubleshooting; AA does not interpret them.
    /// </remarks>
    public static int ToExitCode(this ProcessingOutcome outcome) => outcome switch
    {
        ProcessingOutcome.AutoRetrySubmitted or ProcessingOutcome.ManualReview or ProcessingOutcome.AlreadyRetried => 0,
        ProcessingOutcome.RemediationFailed => 30,
        ProcessingOutcome.InvalidInput => 40,
        _ => 50
    };
}