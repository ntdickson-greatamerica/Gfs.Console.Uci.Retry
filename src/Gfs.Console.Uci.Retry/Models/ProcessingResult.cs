using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gfs.Console.Uci.Retry.Models;

/// <summary>The single JSON object written to stdout. Diagnostic only: AA relies solely on the process exit code. Unknown values are omitted.</summary>
public sealed record ProcessingResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public required ProcessingOutcome Outcome { get; init; }

    /// <summary>The process exit code for this (final) result, from the one mapping in <see cref="ProcessingOutcomeExtensions.ToExitCode"/>.</summary>
    public int ExitCode => Outcome.ToExitCode();

    public UciErrorType? ErrorType { get; init; }
    public string? UciIdentifier { get; init; }
    public required string Message { get; init; }
    public int? AsSectionsInspected { get; init; }
    public int? EquipZipFieldsModified { get; init; }
    public string? OriginalErrorFile { get; init; }
    public string? OriginalUciFile { get; init; }
    public string? RetryUciFile { get; init; }
    public string? OutboundFile { get; init; }
    public string? MarkerFile { get; init; }
    public string? ArchivedErrorFile { get; init; }

    // Internal state for notification decisions. Deliberately not serialized: AA must not interpret processing details.
    [JsonIgnore] public FailureStage FailureStage { get; init; }
    [JsonIgnore] public bool RetryClaimed { get; init; }
    [JsonIgnore] public bool OutboundReleased { get; init; }
    [JsonIgnore] public bool ErrorArchived { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
