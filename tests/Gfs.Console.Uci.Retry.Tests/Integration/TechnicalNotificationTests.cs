using System.Text.Json;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Notifications;
using Gfs.Console.Uci.Retry.Processing;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gfs.Console.Uci.Retry.Tests.Integration;

/// <summary>Every non-zero result attempts exactly one technical notification; exit 0 never sends one.</summary>
public sealed class TechnicalNotificationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-tech-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ErrorFile => Path.Combine(_dir, "e.txt");
    private string Marker => RetryTracker.GetMarkerPath(Path.Combine(_dir, "markers"), "e.txt")!;
    private string Archive => Path.Combine(_dir, "archive");

    private void Write(string content) => File.WriteAllBytes(ErrorFile, ErrorFileEncoding.Encoding.GetBytes(content));

    private void ExistingMarker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        File.WriteAllText(Marker, "{}");
    }

    private sealed class ListProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(lines);
        public void Dispose() { }

        private sealed class ListLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    private (int Exit, JsonElement Json, FakeEmailSender Sender, List<string> Logs) Run(string[]? args = null, FakeEmailSender? sender = null,
        bool notifications = true, bool directories = true)
    {
        sender ??= new FakeEmailSender();
        var logs = new List<string>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestData.Settings)
            .AddInMemoryCollection(TestData.Directories(directories ? TestData.WorkDir(_dir) : null))
            .AddInMemoryCollection(TestData.NotificationSettings(notifications))
            .Build();
        var stdout = new StringWriter();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new ListProvider(logs)));
        var exit = UciRetryApp.Run(args ?? ["--error-file", ErrorFile], stdout, factory, configuration, sender);
        using var doc = JsonDocument.Parse(stdout.ToString()); // exactly one JSON object
        return (exit, doc.RootElement.Clone(), sender, logs);
    }

    private static void AssertOneTechnical(FakeEmailSender sender, int exit)
    {
        Assert.NotEqual(0, exit);
        Assert.Equal(1, sender.TechnicalAttempts);
        Assert.Contains($"Process exit code: {exit}", sender.TechnicalSent!.Body);
        Assert.Null(sender.TechnicalSent.Attachment);
    }

    [Fact]
    public void AutoRetrySubmitted_SendsNoEmail()
    {
        Write(TestData.GeoErrorFile());
        var (exit, _, sender, _) = Run();
        Assert.Equal(0, exit);
        Assert.Empty(sender.Attempted);
    }

    [Fact]
    public void ManualReview_Delivered_SendsOneBusinessEmailAndNoTechnical()
    {
        Write("some unrelated failure text");
        var (exit, _, sender, _) = Run();
        Assert.Equal(0, exit);
        Assert.Equal(1, sender.BusinessAttempts);
        Assert.Equal(0, sender.TechnicalAttempts);
        Assert.Single(Directory.GetFiles(Archive));
    }

    [Fact]
    public void AlreadyRetried_Delivered_SendsOneBusinessEmailAndNoTechnical()
    {
        Write(TestData.GeoErrorFile());
        ExistingMarker();
        var (exit, _, sender, _) = Run();
        Assert.Equal(0, exit);
        Assert.Equal(1, sender.BusinessAttempts);
        Assert.Equal(0, sender.TechnicalAttempts);
        Assert.Single(Directory.GetFiles(Archive));
    }

    [Fact]
    public void ManualReview_BusinessEmailFails_EscalatesOnce_AndKeepsSource()
    {
        Write("some unrelated failure text");
        var (exit, _, sender, _) = Run(sender: new FakeEmailSender(FakeEmailSender.IsBusiness));
        AssertOneTechnical(sender, exit);
        Assert.Equal(NotificationService.MaxAttempts, sender.BusinessAttempts);
        Assert.Single(sender.Sent);
        Assert.True(File.Exists(ErrorFile));
        Assert.False(Directory.Exists(Archive));
    }

    [Fact]
    public void AlreadyRetried_BusinessEmailFails_EscalatesOnce_KeepsMarkerAndSource()
    {
        Write(TestData.GeoErrorFile());
        ExistingMarker();
        var (exit, _, sender, _) = Run(sender: new FakeEmailSender(FakeEmailSender.IsBusiness));
        AssertOneTechnical(sender, exit);
        Assert.True(File.Exists(ErrorFile));
        Assert.Equal("{}", File.ReadAllText(Marker));
        Assert.False(Directory.Exists(Archive));
    }

    [Fact]
    public void InvalidInput_EscalatesOnce()
    {
        var (exit, json, sender, _) = Run(["--bogus"]);
        AssertOneTechnical(sender, exit);
        Assert.Equal("InvalidInput", json.GetProperty("outcome").GetString());
    }

    [Fact]
    public void MissingProcessorConfiguration_EscalatesOnce()
    {
        Write(TestData.GeoErrorFile());
        var (exit, json, sender, _) = Run(directories: false);
        AssertOneTechnical(sender, exit);
        Assert.Equal("ProcessingError", json.GetProperty("outcome").GetString());
        Assert.True(File.Exists(ErrorFile));
    }

    [Fact]
    public void UnexpectedProcessorException_EscalatesOnce()
    {
        Write(TestData.GeoErrorFile());
        using var locked = new FileStream(ErrorFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var (exit, json, sender, _) = Run();
        AssertOneTechnical(sender, exit);
        Assert.Equal("ProcessingError", json.GetProperty("outcome").GetString());
    }

    [Fact]
    public void RemediationFailure_WorkingDirectoryUnavailable_EscalatesOnce()
    {
        Write(TestData.GeoErrorFile());
        File.WriteAllText(TestData.WorkDir(_dir), "not a directory");
        var (exit, json, sender, _) = Run();
        AssertOneTechnical(sender, exit);
        Assert.Equal("RemediationFailed", json.GetProperty("outcome").GetString());
    }

    [Fact]
    public void MarkerIoFailure_EscalatesOnce()
    {
        Write(TestData.GeoErrorFile());
        File.WriteAllText(Path.Combine(_dir, "markers"), "not a directory");
        var (exit, _, sender, _) = Run();
        AssertOneTechnical(sender, exit);
        Assert.False(File.Exists(Path.Combine(_dir, "outbound", "e.txt")));
    }

    [Fact]
    public void OutboundCollisionWithoutMarker_EscalatesOnce_AndLeavesOutboundUntouched()
    {
        Write(TestData.GeoErrorFile());
        var outbound = Path.Combine(_dir, "outbound", "e.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outbound)!);
        File.WriteAllText(outbound, "earlier");
        var (exit, _, sender, _) = Run();
        AssertOneTechnical(sender, exit);
        Assert.Equal("earlier", File.ReadAllText(outbound));
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void ArchiveFailureAfterBusinessEmail_SendsOneBusinessAndOneTechnical_WithoutSensitiveData()
    {
        Write("some unrelated failure text");
        File.WriteAllText(Archive, "not a directory");
        var (exit, _, sender, logs) = Run();
        AssertOneTechnical(sender, exit);
        Assert.Equal(1, sender.BusinessAttempts);
        Assert.Equal(2, sender.Sent.Count);
        AssertClean(sender.TechnicalSent!.Body + sender.TechnicalSent.Subject + string.Join('\n', logs));
    }

    [Fact]
    public void ArchiveFailureAfterOutboundRelease_TechnicalEmailSaysOutboundMayBeReleased()
    {
        Write(TestData.GeoErrorFile());
        File.WriteAllText(Archive, "not a directory");
        var (exit, _, sender, logs) = Run();
        AssertOneTechnical(sender, exit);
        Assert.Contains("Corrected UCI moved outbound: Yes", sender.TechnicalSent!.Body);
        Assert.Contains("Retry marker claimed: Yes", sender.TechnicalSent.Body);
        Assert.Contains("downstream process may already have observed", sender.TechnicalSent.Body);
        AssertClean(sender.TechnicalSent.Body + string.Join('\n', logs));
    }

    [Fact]
    public void TechnicalEmailFailure_IsBounded_NotRecursive_AndPreservesTheOutcome()
    {
        var (exit, json, sender, logs) = Run(["--bogus"], new FakeEmailSender(int.MaxValue));
        Assert.Equal(40, exit);
        Assert.Equal("InvalidInput", json.GetProperty("outcome").GetString());
        Assert.Equal(NotificationService.MaxAttempts, sender.Attempts);
        Assert.Contains(logs, l => l.StartsWith("Error:") && l.Contains("Notification Technical delivery attempt"));
    }

    [Fact]
    public void NotificationsDisabled_ForATechnicalFailure_IsNonZeroAndLogged()
    {
        var (exit, _, sender, logs) = Run(["--bogus"], notifications: false);
        Assert.Equal(40, exit);
        Assert.Equal(0, sender.Attempts);
        Assert.Contains(logs, l => l.StartsWith("Warning:") && l.Contains("notifications are disabled"));
    }

    private static void AssertClean(string text)
    {
        foreach (var secret in new[] { "CU|", "99999", "88888", TestData.FakeSecret, "some unrelated failure text" })
        {
            Assert.DoesNotContain(secret, text);
        }
    }
}