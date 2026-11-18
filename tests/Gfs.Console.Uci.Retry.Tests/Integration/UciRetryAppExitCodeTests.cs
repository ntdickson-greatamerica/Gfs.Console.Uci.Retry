using System.Text.Json;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Notifications;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Integration;

/// <summary>
/// The AA contract: exit 0 = the application handled the error file; non-zero = it did not. Internal outcomes stay distinct.
/// </summary>
public sealed class UciRetryAppExitCodeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-exit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ErrorFile => Path.Combine(_dir, "e.txt");

    private string Write(string content)
    {
        File.WriteAllBytes(ErrorFile, ErrorFileEncoding.Encoding.GetBytes(content));
        return ErrorFile;
    }

    private (int Exit, string Outcome, int JsonExit, FakeEmailSender Sender) Run(string[] args, FakeEmailSender? sender = null, bool notifications = true)
    {
        sender ??= new FakeEmailSender();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TestData.Settings)
            .AddInMemoryCollection(TestData.Directories(TestData.WorkDir(_dir)))
            .AddInMemoryCollection(TestData.NotificationSettings(notifications))
            .Build();
        var stdout = new StringWriter();
        var exit = UciRetryApp.Run(args, stdout, NullLoggerFactory.Instance, configuration, sender);
        using var doc = JsonDocument.Parse(stdout.ToString());
        return (exit, doc.RootElement.GetProperty("outcome").GetString()!, doc.RootElement.GetProperty("exitCode").GetInt32(), sender);
    }

    [Theory]
    [InlineData(ProcessingOutcome.AutoRetrySubmitted, 0)]
    [InlineData(ProcessingOutcome.ManualReview, 0)]
    [InlineData(ProcessingOutcome.AlreadyRetried, 0)]
    [InlineData(ProcessingOutcome.RemediationFailed, 30)]
    [InlineData(ProcessingOutcome.InvalidInput, 40)]
    [InlineData(ProcessingOutcome.ProcessingError, 50)]
    public void ExitCode_IsDerivedFromOutcomeInOnePlace(ProcessingOutcome outcome, int expected) =>
        Assert.Equal(expected, outcome.ToExitCode());

    [Fact]
    public void HandledManualReview_ExitsZero_YetKeepsItsInternalOutcome_AndSendsItsEmail()
    {
        Write("some unrelated failure text");
        var (exit, outcome, jsonExit, sender) = Run(["--error-file", ErrorFile]);

        Assert.Equal(0, exit);
        Assert.Equal(0, jsonExit);
        Assert.Equal("ManualReview", outcome);
        Assert.Single(sender.Sent);
        Assert.False(File.Exists(ErrorFile)); // fully disposed of: archived after the email
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "archive")));
    }

    [Fact]
    public void HandledAlreadyRetried_ExitsZero_YetKeepsItsInternalOutcome()
    {
        Write(TestData.GeoErrorFile());
        var markers = Path.Combine(_dir, "markers");
        Directory.CreateDirectory(markers);
        File.WriteAllText(Gfs.Console.Uci.Retry.Processing.RetryTracker.GetMarkerPath(markers, "e.txt")!, "{}");

        var (exit, outcome, _, sender) = Run(["--error-file", ErrorFile]);
        Assert.Equal(0, exit);
        Assert.Equal("AlreadyRetried", outcome);
        Assert.Single(sender.Sent);
        Assert.False(File.Exists(ErrorFile));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "archive")));
        Assert.Equal("{}", File.ReadAllText(Gfs.Console.Uci.Retry.Processing.RetryTracker.GetMarkerPath(markers, "e.txt")!));
    }

    [Fact]
    public void ManualReview_EmailFailure_IsNonZero_AndErrorFileIsPreserved()
    {
        Write("some unrelated failure text");
        var sender = new FakeEmailSender(int.MaxValue);
        var (exit, outcome, _, _) = Run(["--error-file", ErrorFile], sender);

        Assert.NotEqual(0, exit);
        Assert.Equal("ProcessingError", outcome);
        Assert.Equal(NotificationService.MaxAttempts, sender.BusinessAttempts);
        Assert.Equal(NotificationService.MaxAttempts, sender.TechnicalAttempts); // one bounded escalation, not recursive
        Assert.True(File.Exists(ErrorFile));
        Assert.False(Directory.Exists(Path.Combine(_dir, "archive"))); // not archived without a delivered email
    }

    [Fact]
    public void AlreadyRetried_EmailFailure_IsNonZero()
    {
        Write(TestData.GeoErrorFile());
        var markers = Path.Combine(_dir, "markers");
        Directory.CreateDirectory(markers);
        File.WriteAllText(Gfs.Console.Uci.Retry.Processing.RetryTracker.GetMarkerPath(markers, "e.txt")!, "{}");

        var (exit, _, _, _) = Run(["--error-file", ErrorFile], new FakeEmailSender(int.MaxValue));
        Assert.NotEqual(0, exit);
        Assert.True(File.Exists(ErrorFile));
        Assert.False(Directory.Exists(Path.Combine(_dir, "archive")));
        Assert.Equal("{}", File.ReadAllText(Gfs.Console.Uci.Retry.Processing.RetryTracker.GetMarkerPath(markers, "e.txt")!));
    }

    [Fact]
    public void ManualReview_ArchiveFailureAfterEmail_IsNonZero_WithExactlyOneEmail_AndSourcePreserved()
    {
        Write("some unrelated failure text");
        File.WriteAllText(Path.Combine(_dir, "archive"), "not a directory");
        var (exit, outcome, _, sender) = Run(["--error-file", ErrorFile]);

        Assert.NotEqual(0, exit);
        Assert.Equal("ProcessingError", outcome);
        Assert.Single(sender.Sent, FakeEmailSender.IsBusiness);
        Assert.Equal(1, sender.BusinessAttempts);
        Assert.Equal(1, sender.TechnicalAttempts);
        Assert.True(File.Exists(ErrorFile));
    }

    [Fact]
    public void AlreadyRetried_ArchiveFailureAfterEmail_IsNonZero_KeepsMarker_AndSendsOneEmail()
    {
        Write(TestData.GeoErrorFile());
        var markers = Path.Combine(_dir, "markers");
        Directory.CreateDirectory(markers);
        var marker = Gfs.Console.Uci.Retry.Processing.RetryTracker.GetMarkerPath(markers, "e.txt")!;
        File.WriteAllText(marker, "{}");
        File.WriteAllText(Path.Combine(_dir, "archive"), "not a directory");

        var (exit, _, _, sender) = Run(["--error-file", ErrorFile]);

        Assert.NotEqual(0, exit);
        Assert.Single(sender.Sent, FakeEmailSender.IsBusiness);
        Assert.Equal(1, sender.TechnicalAttempts);
        Assert.Equal("{}", File.ReadAllText(marker));
        Assert.True(File.Exists(ErrorFile));
        Assert.False(File.Exists(Path.Combine(_dir, "outbound", "e.txt")));
    }

    [Fact]
    public void ManualReview_WithoutArchiveDirectory_FailsBeforeSendingAnyBusinessEmail()
    {
        Write("some unrelated failure text");
        var sender = new FakeEmailSender();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(TestData.Settings)
            .AddInMemoryCollection(TestData.NotificationSettings()).Build();
        var exit = UciRetryApp.Run(["--error-file", ErrorFile], new StringWriter(), NullLoggerFactory.Instance, configuration, sender);

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain(sender.Sent, m => m.To.Contains("manual@example.invalid"));
        Assert.True(File.Exists(ErrorFile));
    }

    [Fact]
    public void RequiredNotificationDisabled_IsNonZero_AndNothingIsSent()
    {
        Write("some unrelated failure text");
        var (exit, _, _, sender) = Run(["--error-file", ErrorFile], notifications: false);
        Assert.NotEqual(0, exit);
        Assert.Equal(0, sender.Attempts); // disabled notifications: nothing can be sent (logged by the service)
    }

    [Fact]
    public void MarkerFailure_IsNonZero()
    {
        Write(TestData.GeoErrorFile());
        File.WriteAllText(Path.Combine(_dir, "markers"), "not a directory");
        var (exit, outcome, _, _) = Run(["--error-file", ErrorFile]);

        Assert.NotEqual(0, exit);
        Assert.Equal("RemediationFailed", outcome);
        Assert.True(File.Exists(ErrorFile));
    }

    [Fact]
    public void WorkingFileSystemFailure_IsNonZero()
    {
        Write(TestData.GeoErrorFile());
        File.WriteAllText(TestData.WorkDir(_dir), "not a directory");
        var (exit, _, _, _) = Run(["--error-file", ErrorFile]);
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public void ArchiveFailureAfterOutbound_IsNonZero()
    {
        Write(TestData.GeoErrorFile());
        File.WriteAllText(Path.Combine(_dir, "archive"), "not a directory");
        var (exit, _, _, _) = Run(["--error-file", ErrorFile]);

        Assert.NotEqual(0, exit);
        Assert.True(File.Exists(Path.Combine(_dir, "outbound", "e.txt")));
    }

    [Fact]
    public void InvalidInvocation_IsNonZero()
    {
        var (exit, outcome, _, _) = Run([]);
        Assert.NotEqual(0, exit);
        Assert.Equal("InvalidInput", outcome);
    }

    [Fact]
    public void UnexpectedNotificationFailure_IsNonZero_ForHandledOutcomesOnly()
    {
        var manual = new ProcessingResult { Outcome = ProcessingOutcome.ManualReview, Message = "m" };
        var success = new ProcessingResult { Outcome = ProcessingOutcome.AutoRetrySubmitted, Message = "m" };

        Assert.NotEqual(0, NotificationService.AsUndelivered(manual, "boom").ExitCode);
        Assert.Equal(0, NotificationService.AsUndelivered(success, "boom").ExitCode);
    }

    [Fact]
    public void SuccessfulRetry_CreatesNoResultOrStatusArtifactForAa()
    {
        Write(TestData.GeoErrorFile());
        var (exit, outcome, _, _) = Run(["--error-file", ErrorFile]);

        Assert.Equal(0, exit);
        Assert.Equal("AutoRetrySubmitted", outcome);
        var files = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dir, f)).Order().ToList();
        Assert.Equal(3, files.Count); // outbound UCI, archived error and the single retry marker
        Assert.Contains(files, f => f == Path.Combine("outbound", "e.txt"));
        Assert.Contains(files, f => f.StartsWith("archive" + Path.DirectorySeparatorChar + "e_"));
        Assert.Contains(files, f => f == Path.Combine("markers", "e.geo-retry.json"));
    }
}
