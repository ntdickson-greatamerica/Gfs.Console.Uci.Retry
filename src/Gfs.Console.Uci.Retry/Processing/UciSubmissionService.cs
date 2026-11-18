using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;
using Microsoft.Extensions.Logging;

namespace Gfs.Console.Uci.Retry.Processing;

/// <summary>Validated destinations for one error file. Every name is the original error file's name; nothing from the file content is used.</summary>
public sealed record SubmissionPlan(string FileName, string Stem, string OutboundPath, string ArchiveDirectory);

/// <summary>Non-sensitive outcome of a move step. <see cref="Moved"/> is true once the destination exists.</summary>
public sealed record SubmissionResult(bool Success, bool Moved, string? Failure = null, string? Path = null);

/// <summary>
/// Moves already corrected and validated files: working UCI -> outbound (one non-overwriting move), then original error -> archive.
/// It does not classify, parse the error file, correct ZIPs or decide eligibility, and it never overwrites or deletes a destination.
/// </summary>
/// <remarks>
/// Moves (not copies) are used so there is never a second corrected UCI or an intermediate file for the downstream
/// process to pick up. Both moves use overwrite:false so a destination that appears between the check and the move is
/// reported as a failure instead of being replaced. The archive receives the ORIGINAL error file, named
/// {base}_{yyyyMMdd_HHmmss}{ext} in UTC; all names derive from the original error file name only.
/// </remarks>
public sealed class UciSubmissionService(ProcessorOptions options, ILogger logger)
{
    private const string RequiredExtension = ".txt";
    private static readonly char[] PlaceholderChars = ['{', '}', '%', '<', '>'];

    /// <summary>Test seam invoked with (stage, destination) at "BeforeOutboundMove" and "BeforeArchiveMove".</summary>
    internal Action<string, string>? TestHook { get; set; }

    /// <summary>Clock for the archive timestamp (UTC, like all persisted timestamps); replaceable in tests.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Working file name: <c>&lt;stem&gt;.working.&lt;guid&gt;.txt</c>.</summary>
    public static string WorkingFileName(string stem) => $"{stem}.working.{Guid.NewGuid():N}{RequiredExtension}";

    /// <summary>Derives every destination from the error file's name only; false when the name is unusable.</summary>
    public bool TryPlan(string errorFile, out SubmissionPlan? plan, out string? failure)
    {
        plan = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(options.OutboundDirectory) || string.IsNullOrWhiteSpace(options.ArchiveDirectory))
        {
            throw new InvalidOperationException("Processor:OutboundDirectory and Processor:ArchiveDirectory must be configured.");
        }

        var name = Path.GetFileName(errorFile);
        if (string.IsNullOrEmpty(name) || name is "." or ".." || Path.IsPathRooted(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(PlaceholderChars) >= 0
            || name != name.Trim() || name.EndsWith('.') || name.StartsWith('.')
            || !name.EndsWith(RequiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            failure = "The error file name cannot be used to name the outbound, marker or archive files.";
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        if (RetryTracker.GetMarkerPath(options.RetryMarkerDirectory, name) is null)
        {
            failure = "The error file name cannot be used to name the retry marker.";
            return false;
        }

        try
        {
            var outbound = Resolve(options.OutboundDirectory, name);
            var archive = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ArchiveDirectory));
            if (outbound is null)
            {
                failure = "The outbound file path does not resolve inside its configured directory.";
                return false;
            }
            plan = new SubmissionPlan(name, stem, outbound, archive);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = "The outbound or archive directory is not a valid path.";
            return false;
        }
    }

    /// <summary>Verifies the working file byte for byte and re-validates it structurally. Null means valid.</summary>
    public static string? VerifyFile(string path, byte[] corrected, UciDocument original, IReadOnlyList<ZipReplacement> plan)
    {
        try
        {
            var actual = File.ReadAllBytes(path);
            if (!actual.AsSpan().SequenceEqual(corrected))
            {
                return "bytes differ from the corrected UCI";
            }
            return GeoCodeRemediator.Validate(original, plan, ErrorFileEncoding.Encoding.GetString(actual));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the file could not be read ({ex.GetType().Name})";
        }
    }

    /// <summary>One non-overwriting move of the working file to the final outbound name, then verification of the result.</summary>
    public SubmissionResult MoveOutbound(SubmissionPlan plan, string workingFile, byte[] corrected, UciDocument original, IReadOnlyList<ZipReplacement> replacements)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.OutboundPath)!);
            TestHook?.Invoke("BeforeOutboundMove", plan.OutboundPath);
            File.Move(workingFile, plan.OutboundPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError("Outbound move failed ({Error}).", ex.GetType().Name);
            return new SubmissionResult(false, false, File.Exists(plan.OutboundPath)
                ? "The final outbound file appeared during release; it was not overwritten."
                : $"The corrected UCI could not be moved to the outbound directory ({ex.GetType().Name}).");
        }

        if (File.Exists(workingFile))
        {
            return new SubmissionResult(false, true, "The working file still exists after the outbound move.");
        }
        var problem = File.Exists(plan.OutboundPath) ? VerifyFile(plan.OutboundPath, corrected, original, replacements) : "the file is missing";
        return problem is null
            ? new SubmissionResult(true, true)
            : new SubmissionResult(false, true, $"The final outbound file failed verification: {problem}");
    }

    /// <summary>Archive name <c>{base}_{yyyyMMdd_HHmmss}{ext}</c>, from the original error file name only.</summary>
    internal static string ArchiveFileName(string fileName, DateTime utc) =>
        $"{Path.GetFileNameWithoutExtension(fileName)}_{utc:yyyyMMdd_HHmmss}{Path.GetExtension(fileName)}";

    /// <summary>Moves the original error file, unchanged, to the archive under a timestamped name without overwriting; fails closed if that name exists.</summary>
    public SubmissionResult ArchiveError(SubmissionPlan plan, string errorFile, byte[] originalBytes)
    {
        var archivePath = string.Empty;
        try
        {
            Directory.CreateDirectory(plan.ArchiveDirectory);
            archivePath = Path.Combine(plan.ArchiveDirectory, ArchiveFileName(plan.FileName, UtcNow()));
            TestHook?.Invoke("BeforeArchiveMove", archivePath);
            File.Move(errorFile, archivePath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError("Error-file archive move failed ({Error}).", ex.GetType().Name);
            return new SubmissionResult(false, false, archivePath.Length > 0 && File.Exists(archivePath)
                ? "An archive file with this name already exists; it was not overwritten."
                : $"The original error file could not be moved to the archive ({ex.GetType().Name}).");
        }

        try
        {
            if (File.Exists(errorFile))
            {
                return new SubmissionResult(false, true, "The original error file still exists at its source after archival.");
            }
            return File.Exists(archivePath) && File.ReadAllBytes(archivePath).AsSpan().SequenceEqual(originalBytes)
                ? new SubmissionResult(true, true, Path: archivePath)
                : new SubmissionResult(false, true, "The archived error file is missing or differs from the original.", archivePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SubmissionResult(false, true, $"The archived error file could not be verified ({ex.GetType().Name}).", archivePath);
        }
    }

    /// <summary>
    /// Archives an error file that was fully handled without a retry (manual review / already retried) through the same
    /// non-overwriting, byte-verified, timestamped move used after a successful auto-retry.
    /// </summary>
    public SubmissionResult ArchiveHandledError(string errorFile)
    {
        if (string.IsNullOrWhiteSpace(options.ArchiveDirectory))
        {
            throw new InvalidOperationException("Processor:ArchiveDirectory is not configured.");
        }

        byte[] originalBytes;
        try
        {
            originalBytes = File.ReadAllBytes(errorFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SubmissionResult(false, false, $"The original error file could not be read before archival ({ex.GetType().Name}).");
        }

        var name = Path.GetFileName(errorFile);
        var archive = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ArchiveDirectory));
        return ArchiveError(new SubmissionPlan(name, Path.GetFileNameWithoutExtension(name), "", archive), errorFile, originalBytes);
    }

    private static string? Resolve(string directory, string name)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.GetFullPath(Path.Combine(root, name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetDirectoryName(full), root, comparison) && string.Equals(Path.GetFileName(full), name, StringComparison.Ordinal)
            ? full : null;
    }
}
