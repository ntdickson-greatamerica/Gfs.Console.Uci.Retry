namespace Gfs.Console.Uci.Retry.Logging;

/// <summary>Bound from "Logging:Log4Net". File logging is off unless explicitly enabled; production values are supplied externally.</summary>
public sealed class Log4NetOptions
{
    public const string SectionName = "Logging:Log4Net";

    public bool Enabled { get; set; }

    /// <summary>Directory for the daily UciRetry_yyyyMMdd.log files. Required when <see cref="Enabled"/>.</summary>
    public string Directory { get; set; } = "";
}
