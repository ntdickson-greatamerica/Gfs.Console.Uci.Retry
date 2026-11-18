namespace Gfs.Console.Uci.Retry.Configuration;

/// <summary>Bound from the "Processor" section of appsettings.json. Values are environment specific.</summary>
public sealed class ProcessorOptions
{
    public const string SectionName = "Processor";

    public string RetryWorkingDirectory { get; set; } = "";
    public string RetryMarkerDirectory { get; set; } = "";
    public string ArchiveDirectory { get; set; } = "";
    public string OutboundDirectory { get; set; } = "";
    public string GeoCodeErrorIdentifier { get; set; } = "";
    public string ErrorFileUciIdentifierPattern { get; set; } = "";
}
