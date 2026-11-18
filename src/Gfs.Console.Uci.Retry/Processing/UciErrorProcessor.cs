using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;
using Microsoft.Extensions.Logging;

namespace Gfs.Console.Uci.Retry.Processing;

/// <summary>Result of analyzing one error file: what kind of error it is, its identifier, and the parsed embedded UCI when safe to proceed.</summary>
public sealed record ErrorFileAnalysis
{
    public required UciErrorType ErrorType { get; init; }
    public string? UciIdentifier { get; init; }
    public EmbeddedUci? EmbeddedUci { get; init; }
    public UciDocument? Document { get; init; }
    public string? FailureReason { get; init; }

    public bool IsReadyForRemediation =>
        ErrorType == UciErrorType.GeoCode && UciIdentifier is not null && EmbeddedUci is not null
        && Document is { AssetGroups.Count: > 0 };
}

/// <summary>
/// Orchestrates the processing of exactly ONE error file; Automation Anywhere owns only iteration; the application owns the complete disposition, including notifications.
/// </summary>
/// <remarks>
/// Successful lifecycle:
/// <code>
/// error file
///   -> classify, extract embedded UCI, parse/validate
///   -> remediate AS EQUIP.ZIP and create + validate the working upload (RetryWorkingDirectory)
///   -> claim the retry marker (atomic; consumes the one automatic retry)
///   -> MOVE the working upload to OutboundDirectory
///   -> MOVE the ORIGINAL error file to ArchiveDirectory under a timestamped name
/// </code>
/// The archive holds the original error file, not the corrected UCI. Every file this app creates is named from the
/// original error file name; the contract/UCI identifier is diagnostic metadata and never names a file.
/// <para>
/// Failure strategy: ambiguous or unsafe business/data conditions fail closed to manual review before anything is
/// claimed or written outbound. Once the marker is claimed, the retry is spent: a later filesystem failure returns
/// RemediationFailed, keeps the marker and any already-moved outbound file (the downstream transfer may have seen it),
/// never rolls back, and never retries automatically.
/// </para>
/// </remarks>
public sealed class UciErrorProcessor(ProcessorOptions options, ILogger logger)
{
    private readonly ErrorClassifier _classifier = new(options);
    private readonly UciIdentifierExtractor _identifierExtractor = new(options);
    private readonly RetryTracker _tracker = new(options, logger);
    private readonly UciSubmissionService _submission = new(options, logger);

    internal UciSubmissionService Submission => _submission;

    /// <summary>Reads the error file and decides whether it is a supported, safely processable geo-code failure. Writes nothing.</summary>
    public ErrorFileAnalysis Analyze(string errorFile)
    {
        var text = ErrorFileEncoding.ReadAll(errorFile);

        var hasUci = EmbeddedUciExtractor.TryExtract(text, out var uci, out var uciFailure);
        var outside = hasUci
            ? new[] { text[..uci!.StartOffset], text[uci.EndOffset..] }
            : [text];

        // Classify only text OUTSIDE the UCI so a message-like value inside the payload can never trigger a retry.
        var errorType = _classifier.Classify(hasUci ? outside[0] : text);
        logger.LogInformation("Error classified as {ErrorType}.", errorType);
        if (errorType != UciErrorType.GeoCode)
        {
            logger.LogInformation("Error file is not a supported error type.");
            return new ErrorFileAnalysis { ErrorType = errorType };
        }

        if (!hasUci)
        {
            return new ErrorFileAnalysis { ErrorType = errorType, FailureReason = uciFailure };
        }

        if (!_identifierExtractor.TryExtract(outside, out var identifier, out var idFailure))
        {
            return new ErrorFileAnalysis { ErrorType = errorType, FailureReason = idFailure };
        }

        if (!UciDocumentParser.TryParse(uci!.Text, out var document, out var parseFailure))
        {
            return new ErrorFileAnalysis { ErrorType = errorType, UciIdentifier = identifier, EmbeddedUci = uci, FailureReason = parseFailure };
        }

        if (document!.AssetGroups.Count == 0)
        {
            return new ErrorFileAnalysis
            {
                ErrorType = errorType, UciIdentifier = identifier, EmbeddedUci = uci, Document = document,
                FailureReason = "The UCI contains no AS sections, so there is no EQUIP.ZIP to correct."
            };
        }

        logger.LogInformation("GeoCode disposition selected; embedded UCI length {UciLength}, AS sections {AsSections}.",
            uci.Text.Length, document.AssetGroups.Count);
        return new ErrorFileAnalysis { ErrorType = errorType, UciIdentifier = identifier, EmbeddedUci = uci, Document = document };
    }

    /// <summary>Turns an analysis into the result for AA, running remediation, marker claim, outbound release and archival when eligible.</summary>
    public ProcessingResult ToResult(ErrorFileAnalysis analysis, string errorFile)
    {
        if (analysis.IsReadyForRemediation)
        {
            return Remediate(analysis, errorFile);
        }

        var message = analysis switch
        {
            { ErrorType: UciErrorType.GeoCode } => $"Geo-code error identified, but it cannot be processed safely: {analysis.FailureReason}",
            _ => "Error is not a supported auto-retry error type."
        };

        logger.LogInformation("ManualReview disposition selected: {Reason}", message);
        return new ProcessingResult
        {
            Outcome = ProcessingOutcome.ManualReview,
            ErrorType = analysis.ErrorType,
            UciIdentifier = analysis.UciIdentifier,
            Message = message,
            OriginalErrorFile = errorFile
        };
    }

    // Correct and validate, write the working file, claim the one-time retry, move the working file outbound, archive the original error.
    private ProcessingResult Remediate(ErrorFileAnalysis analysis, string errorFile)
    {
        var remediation = GeoCodeRemediator.Remediate(analysis.Document!);
        var identifier = analysis.UciIdentifier!;

        ProcessingResult Result(ProcessingOutcome outcome, string message, string? workingFile = null, int? modified = null,
            string? markerFile = null, string? outboundFile = null, string? archivedErrorFile = null,
            FailureStage stage = FailureStage.None, bool retryClaimed = false) => new()
        {
            Outcome = outcome,
            ErrorType = analysis.ErrorType,
            UciIdentifier = identifier,
            AsSectionsInspected = remediation.AsSectionsInspected,
            EquipZipFieldsModified = modified,
            RetryUciFile = workingFile,
            MarkerFile = markerFile,
            OutboundFile = outboundFile,
            ArchivedErrorFile = archivedErrorFile,
            FailureStage = stage,
            RetryClaimed = retryClaimed,
            OutboundReleased = outboundFile is not null,
            ErrorArchived = archivedErrorFile is not null,
            Message = message,
            OriginalErrorFile = errorFile
        };

        switch (remediation.Status)
        {
            case GeoCodeRemediationStatus.NotEligible:
                logger.LogInformation("Geo-code remediation not applicable: {Reason}", remediation.FailureReason);
                return Result(ProcessingOutcome.ManualReview, $"Geo-code error identified, but it cannot be corrected automatically: {remediation.FailureReason}");

            case GeoCodeRemediationStatus.Failed:
                logger.LogError("Geo-code remediation validation failed: {Reason}", remediation.FailureReason);
                return Result(ProcessingOutcome.RemediationFailed, $"Remediation failed: {remediation.FailureReason}", stage: FailureStage.BeforeClaim);
        }

        RequireConfiguration();

        // Every created file is named after the original error file; the UCI identifier is diagnostic metadata only.
        if (!_submission.TryPlan(errorFile, out var plan, out var planFailure))
        {
            logger.LogError("Error file name rejected: {Reason}", planFailure);
            return Result(ProcessingOutcome.RemediationFailed, $"Remediation failed: {planFailure}", stage: FailureStage.BeforeClaim);
        }
        // The marker is authoritative: an existing one means the retry is consumed, whatever is (or is not) in the outbound folder.
        // This is only a read-only existence check; the atomic claim below still decides races.
        var existingMarker = RetryTracker.GetMarkerPath(options.RetryMarkerDirectory!, plan!.FileName);
        if (existingMarker is not null && File.Exists(existingMarker))
        {
            logger.LogWarning("The automatic retry for {ErrorFile} was already consumed.", plan.FileName);
            return Result(ProcessingOutcome.AlreadyRetried, "The automatic retry for this error file was already consumed; nothing was released.",
                markerFile: existingMarker, retryClaimed: true);
        }

        // Pre-claim checks fail closed without consuming the retry; the claim is the point of no return.
        if (File.Exists(plan.OutboundPath))
        {
            logger.LogError("The final outbound file already exists; nothing was claimed or released.");
            return Result(ProcessingOutcome.RemediationFailed, "Remediation failed: the final outbound file already exists and was not overwritten.",
                stage: FailureStage.BeforeClaim);
        }

        var corrected = ErrorFileEncoding.Encoding.GetBytes(remediation.CorrectedContent!);
        if (!TryWriteWorkingFile(plan.Stem, corrected, out var workingFile, out var writeFailure))
        {
            logger.LogError("Writing the corrected UCI failed: {Reason}", writeFailure);
            return Result(ProcessingOutcome.RemediationFailed, $"Remediation failed: {writeFailure}", stage: FailureStage.BeforeClaim);
        }

        var workingProblem = UciSubmissionService.VerifyFile(workingFile!, corrected, analysis.Document!, remediation.Replacements);
        if (workingProblem is not null)
        {
            logger.LogError("Working file validation failed: {Reason}", workingProblem);
            DeleteQuietly(workingFile!);
            return Result(ProcessingOutcome.RemediationFailed, $"Remediation failed: the working file failed validation: {workingProblem}",
                stage: FailureStage.BeforeClaim);
        }

        logger.LogInformation("Corrected UCI created: {ModifiedCount} of {AsCount} EQUIP.ZIP fields modified.",
            remediation.EquipZipFieldsModified, remediation.AsSectionsInspected);

        // The marker must exist before the file is released outbound, so the downstream process can never consume a retry that was not recorded.
        var claim = _tracker.TryClaim(new RetryMarker
        {
            OriginalFileName = plan.FileName,
            UciIdentifier = identifier,
            ErrorType = analysis.ErrorType,
            Status = RetryMarkerStatus.Prepared,
            WorkingFile = workingFile,
            AsSectionsInspected = remediation.AsSectionsInspected,
            EquipZipFieldsModified = remediation.EquipZipFieldsModified,
            CreatedUtc = DateTimeOffset.UtcNow
        });

        switch (claim.Status)
        {
            case RetryClaimStatus.AlreadyRetried:
                logger.LogWarning("The automatic retry for {ErrorFile} was already consumed.", plan.FileName);
                DeleteQuietly(workingFile!);
                return Result(ProcessingOutcome.AlreadyRetried, "The automatic retry for this error file was already consumed; nothing was released.",
                    markerFile: claim.MarkerFile, retryClaimed: true);

            case RetryClaimStatus.Failed:
                // A marker that exists (partial write) stays; the working file is only discarded when no claim exists.
                if (claim.MarkerFile is null)
                {
                    DeleteQuietly(workingFile!);
                }
                return Result(ProcessingOutcome.RemediationFailed, $"Remediation failed: {claim.FailureReason}",
                    claim.MarkerFile is null ? null : workingFile, markerFile: claim.MarkerFile,
                    stage: claim.MarkerFile is null ? FailureStage.BeforeClaim : FailureStage.AfterClaim, retryClaimed: claim.MarkerFile is not null);
        }

        logger.LogInformation("Retry marker claimed for {ErrorFile}.", plan.FileName);

        // From here the retry is permanently consumed: every failure retains the marker and any moved file, and needs manual investigation.
        ProcessingResult PostClaimFailure(string reason, string? outboundFile, FailureStage stage, string? archivedErrorFile = null)
        {
            logger.LogError("Post-claim failure for {ErrorFile}: {Reason}", plan.FileName, reason);
            return Result(ProcessingOutcome.RemediationFailed, $"Remediation failed after the retry was claimed; manual investigation is required: {reason}",
                File.Exists(workingFile!) ? workingFile : null, remediation.EquipZipFieldsModified, claim.MarkerFile, outboundFile,
                archivedErrorFile, stage, retryClaimed: true);
        }

        // Read before the move so the archived copy can be compared byte for byte with the original afterwards.
        byte[] originalBytes;
        try
        {
            originalBytes = File.ReadAllBytes(errorFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PostClaimFailure($"The original error file could not be read before release ({ex.GetType().Name}).", null, FailureStage.AfterClaim);
        }

        var move = _submission.MoveOutbound(plan, workingFile!);
        if (!move.Success)
        {
            return PostClaimFailure(move.Failure!, move.Moved ? plan.OutboundPath : null,
                move.Moved ? FailureStage.AfterOutboundMove : FailureStage.AfterClaim);
        }

        logger.LogInformation("Corrected UCI moved outbound as {OutboundFile}.", Path.GetFileName(plan.OutboundPath));
        var archive = _submission.ArchiveError(plan, errorFile, originalBytes);
        if (!archive.Success)
        {
            return PostClaimFailure(archive.Failure!, plan.OutboundPath, FailureStage.ArchiveFailed);
        }


        logger.LogInformation("Corrected UCI for {ErrorFile} released for retry and the original error file archived.", plan.FileName);
        return Result(ProcessingOutcome.AutoRetrySubmitted, "Corrected UCI was released for retry and the original error file was archived.",
            modified: remediation.EquipZipFieldsModified, markerFile: claim.MarkerFile, outboundFile: plan.OutboundPath, archivedErrorFile: archive.Path);
    }

    private void RequireConfiguration()
    {
        foreach (var (name, value) in new[]
        {
            ("RetryWorkingDirectory", options.RetryWorkingDirectory), ("RetryMarkerDirectory", options.RetryMarkerDirectory),
            ("ArchiveDirectory", options.ArchiveDirectory), ("OutboundDirectory", options.OutboundDirectory)
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Processor:{name} is not configured.");
            }
        }
    }


    /// <summary>Writes a brand-new file (never overwrites) and verifies it byte for byte; an invalid artifact is deleted.</summary>
    private bool TryWriteWorkingFile(string stem, byte[] bytes, out string? path, out string? failure)
    {
        if (string.IsNullOrWhiteSpace(options.RetryWorkingDirectory))
        {
            throw new InvalidOperationException("Processor:RetryWorkingDirectory is not configured.");
        }

        path = null;
        failure = null;
        var candidate = "";

        try
        {
            var directory = Path.GetFullPath(options.RetryWorkingDirectory);
            Directory.CreateDirectory(directory);
            candidate = Path.Combine(directory, UciSubmissionService.WorkingFileName(stem));

            using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }

            if (!File.ReadAllBytes(candidate).AsSpan().SequenceEqual(bytes))
            {
                DeleteQuietly(candidate);
                failure = "The written working file did not match the validated corrected UCI.";
                return false;
            }

            path = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            if (candidate.Length > 0)
            {
                DeleteQuietly(candidate);
            }
            failure = $"The corrected UCI could not be written to the working directory ({ex.GetType().Name}).";
            return false;
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete invalid working file {WorkingFile}.", path);
        }
    }
}
