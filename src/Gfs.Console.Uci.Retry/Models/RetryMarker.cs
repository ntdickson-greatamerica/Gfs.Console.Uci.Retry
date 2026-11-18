namespace Gfs.Console.Uci.Retry.Models;

public enum RetryMarkerStatus
{
    /// <summary>The retry was claimed. Written once at claim time and never updated; diagnostic only.</summary>
    Prepared
}

/// <summary>
/// Content of <c>&lt;errorFileName&gt;.geo-retry.json</c>. Diagnostic only: the marker's existence, not its content, consumes the retry.
/// Must never contain UCI content, ZIP values, or customer data.
/// </summary>
public sealed record RetryMarker
{
    /// <summary>File name of the original error file (e.g. ex13314697.txt). Its stem names the marker.</summary>
    public required string OriginalFileName { get; init; }

    /// <summary>Extracted contract/UCI identifier. Diagnostic metadata only; never used for file names.</summary>
    public required string UciIdentifier { get; init; }

    public required UciErrorType ErrorType { get; init; }
    public RetryMarkerStatus Status { get; init; } = RetryMarkerStatus.Prepared;
    public string? WorkingFile { get; init; }
    public int? AsSectionsInspected { get; init; }
    public int? EquipZipFieldsModified { get; init; }

    /// <summary>UTC time the marker was created.</summary>
    public required DateTimeOffset CreatedUtc { get; init; }
}
