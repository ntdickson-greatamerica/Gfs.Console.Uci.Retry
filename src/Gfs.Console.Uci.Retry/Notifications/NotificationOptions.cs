namespace Gfs.Console.Uci.Retry.Notifications;

/// <summary>
/// Bound from the "Notifications" section. The checked-in values are non-operational: delivery is disabled and nothing
/// environment specific is stored. Host, recipients and secrets come from external configuration
/// (UCIRETRY_Notifications__*); credentials are never read from appsettings.json.
/// </summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; }
    public bool UseSsl { get; set; } = true;
    public string SmtpUserName { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public List<string> ManualReviewRecipients { get; set; } = [];
    public List<string> TechnicalRecipients { get; set; } = [];
    public string SubjectPrefix { get; set; } = "";

    /// <summary>Largest original error file that may be attached; a larger file is left out and the message says so.</summary>
    public long MaxAttachmentBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>The contract/UCI identifier is business data; it is only included in messages when this is explicitly approved.</summary>
    public bool IncludeUciIdentifier { get; set; }
}
