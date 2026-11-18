using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Fixtures;
internal static class TestData
{
    // Same values as appsettings.json.
    public static readonly Dictionary<string, string?> Settings = new()
    {
        ["Processor:GeoCodeErrorIdentifier"] = "is an inactive GeoCode associated with Zip Code",
        ["Processor:ErrorFileUciIdentifierPattern"] = @"(?<![\d-])(?<id>\d{3}-\d{7}-\d{3})(?![\d-])"
    };

    public static IConfiguration Configuration(string? workingDirectory = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(Settings)
            .AddInMemoryCollection(Directories(workingDirectory)).Build();

    // Marker, archive and outbound directories are siblings of the working directory.
    public static Dictionary<string, string?> Directories(string? workingDirectory)
    {
        var parent = string.IsNullOrEmpty(workingDirectory) ? null : System.IO.Path.GetDirectoryName(workingDirectory);
        string Sibling(string name) => parent is null ? "" : System.IO.Path.Combine(parent, name);
        return new()
        {
            ["Processor:RetryWorkingDirectory"] = workingDirectory ?? "",
            ["Processor:RetryMarkerDirectory"] = Sibling("markers"),
            ["Processor:ArchiveDirectory"] = Sibling("archive"),
            ["Processor:OutboundDirectory"] = Sibling("outbound")
        };
    }

    // Enabled notification settings pointing at nothing real; delivery always goes through a fake sender.
    public const string FakeSecret = "s3cret-not-real";

    public static Dictionary<string, string?> NotificationSettings(bool enabled = true) => new()
    {
        ["Notifications:Enabled"] = enabled.ToString(),
        ["Notifications:SmtpHost"] = "smtp.invalid",
        ["Notifications:SmtpPort"] = "25",
        ["Notifications:SmtpPassword"] = FakeSecret,
        ["Notifications:FromAddress"] = "uci-retry@example.invalid",
        ["Notifications:ManualReviewRecipients:0"] = "manual@example.invalid",
        ["Notifications:TechnicalRecipients:0"] = "tech@example.invalid",
        ["Notifications:SubjectPrefix"] = "[TEST]"
    };
    public static ProcessorOptions Options(string? workingDirectory = null) =>
        Configuration(workingDirectory).GetSection(ProcessorOptions.SectionName).Get<ProcessorOptions>()!;

    public const string GeoMessage =
        "11*222*3333 is an inactive GeoCode associated with Zip Code 99999-0000.  It cannot be entered on a new asset location for Asset ID 4444.";

    public const string Identifier = "100-2000000-300";

    // Synthetic, grammar-valid UCI: two asset groups, repeated RP/MB/MI, empty fields.
    public static readonly string Uci = UciBuilder.Join(
        UciBuilder.Cu, UciBuilder.Rp("", "r1"), UciBuilder.Rp("r2"), UciBuilder.Co,
        UciBuilder.As("99999-0000"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami,
        UciBuilder.As("88888"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami,
        UciBuilder.Mb("m1", "m2"), UciBuilder.Mb("", "m3"), UciBuilder.Mi("n1"), UciBuilder.Mi("n2"), UciBuilder.Rn);

    public static string GeoErrorFile(string? uci = null, string after = "\r\n" + Identifier) =>
        "\r\nfile.txt\r\n " + GeoMessage + "\u00FD 0\u00FD " + Identifier + " Contract not created.\r\n" + (uci ?? Uci) + "{" + after;

    public static (UciErrorProcessor Processor, string Path) Create(string content, string dir, out string path)
    {
        path = System.IO.Path.Combine(dir, "e.txt");
        File.WriteAllBytes(path, ErrorFileEncoding.Encoding.GetBytes(content));
        return (new UciErrorProcessor(Options(WorkDir(dir)), NullLogger.Instance), path);
    }

    public static string WorkDir(string dir) => System.IO.Path.Combine(dir, "work");
}
