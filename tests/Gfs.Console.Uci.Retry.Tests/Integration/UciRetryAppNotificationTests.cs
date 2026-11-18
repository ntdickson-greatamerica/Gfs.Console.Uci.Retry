using System.Text.Json;
using Gfs.Console.Uci.Retry.Notifications;
using Gfs.Console.Uci.Retry.Processing;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Integration;

/// <summary>End-to-end notification behavior through <see cref="UciRetryApp.Run"/> with a fake sender and real temp files.</summary>
public sealed class UciRetryAppNotificationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-notify-e2e-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Work => TestData.WorkDir(_dir);
    private string Outbound => Path.Combine(_dir, "outbound");
    private string Archive => Path.Combine(_dir, "archive");
    private string Markers => Path.Combine(_dir, "markers");
    private string ErrorFile => Path.Combine(_dir, "e.txt");

    private string Write(string content)
    {
        File.WriteAllBytes(ErrorFile, Gfs.Console.Uci.Retry.Models.ErrorFileEncoding.Encoding.GetBytes(content));
        return ErrorFile;
    }

    private (int Exit, System.Text.Json.JsonElement Json, FakeEmailSender Sender, string Stdout) Run(string[] args, bool noDirectories = false)
    {
        var sender = new FakeEmailSender();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestData.Settings)
            .AddInMemoryCollection(TestData.Directories(noDirectories ? null : Work))
            .AddInMemoryCollection(TestData.NotificationSettings())
            .Build();
        var stdout = new StringWriter();
        var exit = UciRetryApp.Run(args, stdout, LoggerFactory.Create(_ => { }), configuration, sender);
        using var doc = JsonDocument.Parse(stdout.ToString()); // exactly one JSON object, nothing else on stdout
        return (exit, doc.RootElement.Clone(), sender, stdout.ToString());
    }

    private string[] Archived() =>
        Directory.Exists(Archive) ? Directory.GetFiles(Archive).Select(f => Path.GetFileName(f)!).Order().ToArray() : [];

    private static void AssertTimestampedArchive(string name) =>
        Assert.Matches(@"^e_\d{8}_\d{6}\.txt$", name);

    private static void AssertClean(EmailMessage message)
    {
        foreach (var text in new[] { message.Subject, message.Body })
        {
            Assert.DoesNotContain("99999", text);
            Assert.DoesNotContain("88888", text);
            Assert.DoesNotContain("12345-67", text);
            Assert.DoesNotContain(TestData.Identifier, text);
            Assert.DoesNotContain(TestData.FakeSecret, text);
            Assert.DoesNotContain("CU|", text);
        }
    }

    [Fact]
    public void AutoRetrySubmitted_SendsNoEmail_AndKeepsExit0()
    {
        var file = Write(TestData.GeoErrorFile());
        var (exit, json, sender, _) = Run(["--error-file", file]);

        Assert.Equal(0, exit);
        Assert.Equal("AutoRetrySubmitted", json.GetProperty("outcome").GetString());
        Assert.Empty(sender.Sent);
        Assert.Equal(0, sender.Attempts);
        Assert.True(File.Exists(Path.Combine(Outbound, "e.txt")));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void UnknownError_IsManualReview_WithOriginalAttached_AndArchivedAfterNotification()
    {
        var file = Write("some unrelated failure text");
        var original = File.ReadAllBytes(file);
        var (exit, _, sender, _) = Run(["--error-file", file]);

        Assert.Equal(0, exit);
        var message = Assert.Single(sender.Sent);
        Assert.Equal(["manual@example.invalid"], message.To);
        Assert.Equal(original, message.Attachment!.Content);
        Assert.False(File.Exists(file));
        var archived = Assert.Single(Archived());
        AssertTimestampedArchive(archived);
        Assert.DoesNotContain("100-2000000-300", archived);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Archive, archived)));
        AssertClean(message);
    }

    [Fact]
    public void GeoCode_WithNoEligibleCorrection_IsManualReview()
    {
        var uci = UciBuilder.Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("88888"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami, UciBuilder.Rn);
        var file = Write(TestData.GeoErrorFile(uci));
        var (exit, _, sender, _) = Run(["--error-file", file]);

        Assert.Equal(0, exit);
        Assert.Single(sender.Sent);
        Assert.False(File.Exists(file));
        AssertTimestampedArchive(Assert.Single(Archived()));
        Assert.False(Directory.Exists(Outbound));
    }

    [Fact]
    public void UnsupportedEquipZip_IsManualReview_WithoutTheValueInTheMessage()
    {
        var uci = UciBuilder.Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("12345-67"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami, UciBuilder.Rn);
        var file = Write(TestData.GeoErrorFile(uci));
        var (exit, _, sender, _) = Run(["--error-file", file]);

        Assert.Equal(0, exit);
        AssertClean(Assert.Single(sender.Sent));
    }

    [Fact]
    public void AlreadyRetried_NotifiesManualReview_AndKeepsMarker_NoRetry()
    {
        var file = Write(TestData.GeoErrorFile());
        Directory.CreateDirectory(Markers);
        var marker = RetryTracker.GetMarkerPath(Markers, "e.txt")!;
        File.WriteAllText(marker, "{}");
        var (exit, _, sender, _) = Run(["--error-file", file]);

        Assert.Equal(0, exit);
        var message = Assert.Single(sender.Sent);
        Assert.Contains("already exists", message.Body);
        Assert.Contains("prohibited", message.Body);
        Assert.Equal("{}", File.ReadAllText(marker)); // existing marker unchanged
        Assert.False(File.Exists(Path.Combine(Outbound, "e.txt")));
        Assert.False(File.Exists(file));
        AssertTimestampedArchive(Assert.Single(Archived()));
        AssertClean(message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyRetried_TakesPrecedenceOverAnExistingOutboundFile(bool outboundExists)
    {
        var file = Write(TestData.GeoErrorFile());
        Directory.CreateDirectory(Markers);
        var marker = RetryTracker.GetMarkerPath(Markers, "e.txt")!;
        File.WriteAllText(marker, "{}");
        var outbound = Path.Combine(Outbound, "e.txt");
        if (outboundExists)
        {
            Directory.CreateDirectory(Outbound);
            File.WriteAllText(outbound, "prior outbound");
        }

        var (exit, json, sender, _) = Run(["--error-file", file]);

        Assert.Equal(0, exit);
        Assert.Equal("AlreadyRetried", json.GetProperty("outcome").GetString());
        var message = Assert.Single(sender.Sent);
        Assert.Contains("prohibited", message.Body);
        Assert.Equal("{}", File.ReadAllText(marker));
        Assert.Equal(outboundExists ? "prior outbound" : null, File.Exists(outbound) ? File.ReadAllText(outbound) : null);
        Assert.False(File.Exists(file));
        AssertTimestampedArchive(Assert.Single(Archived()));
        Assert.Empty(Directory.GetFiles(_dir, "*.working.*", SearchOption.AllDirectories));
    }
    [Fact]
    public void FailureBeforeClaim_NotifiesTechnical_AndMarkerNotClaimed()
    {
        var file = Write(TestData.GeoErrorFile());
        Directory.CreateDirectory(Outbound);
        File.WriteAllText(Path.Combine(Outbound, "e.txt"), "existing");
        var (exit, _, sender, _) = Run(["--error-file", file]);

        Assert.Equal(30, exit);
        var message = Assert.Single(sender.Sent);
        Assert.Equal(["tech@example.invalid"], message.To);
        Assert.Contains("before the retry marker was claimed", message.Body);
        Assert.Null(message.Attachment);
        Assert.False(File.Exists(RetryTracker.GetMarkerPath(Markers, "e.txt")!));
        AssertClean(message);
    }

    [Fact]
    public void FailureAfterClaim_StatesRetryConsumed_AndMarkerRemains()
    {
        var file = Write(TestData.GeoErrorFile());
        File.WriteAllText(Outbound, "not a directory"); // outbound move cannot create its directory
        var (exit, _, sender, _) = Run(["--error-file", file]);

        Assert.Equal(30, exit);
        var message = Assert.Single(sender.Sent);
        Assert.Contains("considered consumed", message.Body);
        Assert.Contains("Retry marker claimed: Yes", message.Body);
        Assert.True(File.Exists(RetryTracker.GetMarkerPath(Markers, "e.txt")!));
        Assert.True(File.Exists(file));
        AssertClean(message);
    }

    [Fact]
    public void ArchiveFailure_ReportsOutboundSuccess_AndDoesNotRollBack()
    {
        var file = Write(TestData.GeoErrorFile());
        File.WriteAllText(Archive, "not a directory");
        var (exit, _, sender, _) = Run(["--error-file", file]);

        Assert.Equal(30, exit);
        var message = Assert.Single(sender.Sent);
        Assert.Contains("Corrected UCI moved outbound: Yes", message.Body);
        Assert.Contains("archiving the original error file failed", message.Body);
        Assert.Contains("may already have observed", message.Body);
        Assert.True(File.Exists(Path.Combine(Outbound, "e.txt")));
        Assert.True(File.Exists(RetryTracker.GetMarkerPath(Markers, "e.txt")!));
        AssertClean(message);
    }

    [Fact]
    public void InvalidInput_NotifiesTechnical()
    {
        var (exit, _, sender, _) = Run([]);
        Assert.Equal(40, exit);
        var message = Assert.Single(sender.Sent);
        Assert.Equal(["tech@example.invalid"], message.To);
    }

    [Fact]
    public void ProcessingError_FromMissingDirectories_NotifiesTechnical_AndWritesNothing()
    {
        var file = Write(TestData.GeoErrorFile());
        var (exit, _, sender, _) = Run(["--error-file", file], noDirectories: true);

        Assert.Equal(50, exit);
        Assert.Equal(["tech@example.invalid"], Assert.Single(sender.Sent).To);
        Assert.Equal(["e.txt"], Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void BrokenNotificationConfiguration_DoesNotRecurse_AndStdoutStaysOneJson()
    {
        var file = Write("some unrelated failure text");
        var sender = new FakeEmailSender();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestData.Settings).AddInMemoryCollection(TestData.Directories(Work))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:SmtpPort"] = "not-a-number", ["Notifications:Enabled"] = "true" })
            .Build();
        var stdout = new StringWriter();

        var exit = UciRetryApp.Run(["--error-file", file], stdout, NullLoggerFactory.Instance, configuration, sender);

        Assert.Equal(50, exit);
        Assert.Equal(0, sender.Attempts);
        JsonDocument.Parse(stdout.ToString()).Dispose();
        Assert.True(File.Exists(file));
    }
}
