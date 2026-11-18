using System.Text.Json;
using System.Text.Json.Serialization;
using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;
using Microsoft.Extensions.Logging;

namespace Gfs.Console.Uci.Retry.Processing;

public enum RetryClaimStatus { Claimed, AlreadyRetried, Failed }

public sealed record RetryClaimResult(RetryClaimStatus Status, string? MarkerFile = null, string? FailureReason = null);

/// <summary>
/// Persistent one-retry-per-error-file protection. The marker is named after the original error file (never the UCI identifier).
/// </summary>
/// <remarks>
/// Claim = exclusive creation (CreateNew, FileShare.None) of the FINAL marker file. That single atomic filesystem
/// operation is the claim, so two concurrent executions cannot both win. Marker existence alone means the retry is
/// consumed; the content is diagnostic only. An existing marker is therefore never opened,
/// parsed, repaired, overwritten or deleted, so empty, malformed or partial markers (e.g. after a crash) still yield
/// AlreadyRetried. This deliberately favors manual review over any chance of a duplicate submission. If creation
/// succeeds but writing/flushing fails, the marker is left in place and Failed is returned; the retry stays consumed
/// and is never retried automatically.
/// </remarks>
public sealed class RetryTracker
{
    public const string MarkerSuffix = ".geo-retry.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ProcessorOptions _options;
    private readonly ILogger _logger;

    public RetryTracker(ProcessorOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Deterministic marker path for an error file name, or null when the name or resolved path is unsafe.</summary>
    public static string? GetMarkerPath(string markerDirectory, string originalFileName)
    {
        if (string.IsNullOrEmpty(originalFileName) || originalFileName != Path.GetFileName(originalFileName)
            || originalFileName is "." or ".." || Path.IsPathRooted(originalFileName))
            return null;

        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        if (stem.Length == 0 || stem != stem.Trim() || stem.StartsWith('.') || stem.EndsWith('.')
            || stem.Contains("..", StringComparison.Ordinal) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(markerDirectory));
        var path = Path.GetFullPath(Path.Combine(root, stem + MarkerSuffix));
        return string.Equals(Path.GetDirectoryName(path), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? path : null;
    }

    /// <summary>Atomically claims the single automatic retry for the marker's error file. Never overwrites an existing marker.</summary>
    /// <exception cref="InvalidOperationException">RetryMarkerDirectory is not configured.</exception>
    public RetryClaimResult TryClaim(RetryMarker marker)
    {
        if (string.IsNullOrWhiteSpace(_options.RetryMarkerDirectory))
            throw new InvalidOperationException("Processor:RetryMarkerDirectory is not configured.");

        var path = GetMarkerPath(_options.RetryMarkerDirectory, marker.OriginalFileName);
        if (path is null)
            return new RetryClaimResult(RetryClaimStatus.Failed, FailureReason: "The error file name is not safe for use as a marker file name.");

        FileStream stream;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another execution (or an earlier run) already owns the marker; its content is irrelevant.
            return new RetryClaimResult(RetryClaimStatus.AlreadyRetried, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "Retry marker could not be created.");
            return new RetryClaimResult(RetryClaimStatus.Failed, FailureReason: "Retry marker could not be created.");
        }

        try
        {
            using (stream)
            {
                JsonSerializer.Serialize(stream, marker, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            return new RetryClaimResult(RetryClaimStatus.Claimed, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            _logger.LogError(ex, "Retry marker was created but could not be written; it remains in place and the retry is consumed.");
            return new RetryClaimResult(RetryClaimStatus.Failed, path,
                "Retry marker was created but could not be completed; the retry is consumed.");
        }
    }
}
