using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Processing;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Integration;
public sealed class UciErrorProcessorLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Work => TestData.WorkDir(_dir);
    private string Markers => Path.Combine(_dir, "markers");
    private string Archive => Path.Combine(_dir, "archive");
    private string Outbound => Path.Combine(_dir, "outbound");

    private static readonly string Source = UciBuilder.Valid("523021234", "12345", "52302-1234", "") + "|";
    private static readonly string Expected = UciBuilder.Valid("52302", "12345", "52302", "") + "|";

    private string ErrorFile(string name, string? uci = null, string id = TestData.Identifier, string sub = "in")
    {
        var dir = Path.Combine(_dir, sub);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, ErrorFileEncoding.Encoding.GetBytes(TestData.GeoErrorFile(uci ?? Source).Replace(TestData.Identifier, id)));
        return path;
    }

    private ProcessorOptions Options(string? outbound = null, string? archive = null)
    {
        var options = TestData.Options(Work);
        options.OutboundDirectory = outbound ?? Outbound;
        options.ArchiveDirectory = archive ?? Archive;
        return options;
    }

    private static readonly DateTime Now = new(2026, 10, 30, 11, 56, 22, DateTimeKind.Utc);
    private const string Stamp = "_20261030_115622";

    private UciErrorProcessor Processor(string? outbound = null, string? archive = null)
    {
        var processor = new UciErrorProcessor(Options(outbound, archive), NullLogger.Instance);
        processor.Submission.UtcNow = () => Now;
        return processor;
    }

    private static ProcessingResult Run(string path, UciErrorProcessor processor) => processor.ToResult(processor.Analyze(path), path);

    private ProcessingResult Run(string path) => Run(path, Processor());

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string[] Files(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
    private string MarkerPath(string stem) => Path.Combine(Markers, stem + ".geo-retry.json");
    private static string Text(string path) => ErrorFileEncoding.Encoding.GetString(File.ReadAllBytes(path));

    [Fact]
    public void FullWorkflow_MovesCorrectedUciOutbound_AndArchivesTheOriginalError()
    {
        var path = ErrorFile("ex10000001.txt");
        var originalBytes = File.ReadAllBytes(path);
        var processor = Processor();
        var analysis = processor.Analyze(path);

        var result = processor.ToResult(analysis, path);

        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Corrected UCI was released for retry and the original error file was archived.", result.Message);
        Assert.Equal(4, result.AsSectionsInspected);
        Assert.Equal(2, result.EquipZipFieldsModified);
        Assert.Null(result.RetryUciFile);

        // Outbound: exact original name, corrected embedded UCI only.
        var outbound = Assert.Single(Files(Outbound));
        Assert.Equal(Path.Combine(Outbound, "ex10000001.txt"), outbound);
        Assert.Equal(outbound, result.OutboundFile);
        Assert.Equal(Expected, Text(outbound));
        Assert.DoesNotContain('{', Text(outbound));
        Assert.True(Text(outbound).EndsWith('|'));
        Assert.DoesNotContain("GeoCode", Text(outbound));

        // The working file was moved, not copied.
        Assert.Empty(Files(Work));

        // Archive: the ORIGINAL error file, unchanged, under {base}_{yyyyMMdd_HHmmss}{ext}; nothing else.
        var archived = Assert.Single(Files(Archive));
        Assert.Equal(Path.Combine(Archive, "ex10000001" + Stamp + ".txt"), archived);
        Assert.Equal(archived, result.ArchivedErrorFile);
        Assert.Equal(originalBytes, File.ReadAllBytes(archived));
        Assert.NotEqual(File.ReadAllBytes(outbound), File.ReadAllBytes(archived));
        Assert.False(File.Exists(path));

        // Exactly one marker, named after the error file; no companion status file; status stays Prepared and the marker is not rewritten.
        Assert.Equal(MarkerPath("ex10000001"), result.MarkerFile);
        Assert.Equal("Prepared", JsonDocument.Parse(File.ReadAllText(MarkerPath("ex10000001"))).RootElement.GetProperty("status").GetString());
        Assert.Equal(MarkerPath("ex10000001"), Assert.Single(Files(Markers)));
        Assert.Empty(Directory.GetFiles(_dir, "*.geo-retry.status.json", SearchOption.AllDirectories));
        Assert.Equal(Source, analysis.EmbeddedUci!.Text);
    }

    [Fact]
    public void EveryCreatedFile_IsNamedAfterTheErrorFile_NeverTheUciIdentifier()
    {
        var path = ErrorFile("ex10000001.txt", id: "900-1234567-000");
        Run(path);

        var marker = JsonDocument.Parse(File.ReadAllText(MarkerPath("ex10000001"))).RootElement;
        Assert.Equal("900-1234567-000", marker.GetProperty("uciIdentifier").GetString());
        Assert.Equal("ex10000001.txt", marker.GetProperty("originalFileName").GetString());
        Assert.Matches(@"^ex10000001\.working\.[0-9a-f]{32}\.txt$", Path.GetFileName(marker.GetProperty("workingFile").GetString()!));
        Assert.Equal(Work, Path.GetDirectoryName(marker.GetProperty("workingFile").GetString()));

        var created = new[] { Outbound, Archive, Markers }.SelectMany(Files)        .Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                Assert.Equal(new[] { "ex10000001.geo-retry.json", "ex10000001.txt", "ex10000001_20261030_115622.txt" }, created);
        Assert.All(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories), f => Assert.DoesNotContain("1234567-000", Path.GetFileName(f)));
    }

    [Fact]
    public void FinalPipeIsNotAdded_WhenTheSourceHasNone()
    {
        var result = Run(ErrorFile("e.txt", UciBuilder.Valid("523021234")));
        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, result.Outcome);
        Assert.Equal(UciBuilder.Valid("52302"), Text(result.OutboundFile!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"originalFileName\":\"e.txt\",\"status\":\"Submitted\"}")]
    [InlineData("not json at all")]
    [InlineData("{\"originalFileName\":\"e.t")]
    public void ExistingMarkerOfAnyContent_IsAlreadyRetried_AndNothingMoves(string content)
    {
        var path = ErrorFile("e.txt");
        var before = Sha(path);
        Directory.CreateDirectory(Markers);
        File.WriteAllText(MarkerPath("e"), content);

        var result = Run(path);

        Assert.Equal(ProcessingOutcome.AlreadyRetried, result.Outcome);
        Assert.Equal(ProcessingOutcome.AlreadyRetried, result.Outcome);
        Assert.Equal(0, result.ExitCode); // handled once its notification is delivered
        Assert.Equal(MarkerPath("e"), result.MarkerFile);
        Assert.Empty(Files(Outbound));
        Assert.Empty(Files(Archive));
        Assert.Empty(Files(Work));
        Assert.Equal(content, File.ReadAllText(MarkerPath("e")));
        Assert.Equal(before, Sha(path));
    }

    [Fact]
    public void MarkerClaimFailure_IsRemediationFailed_WithNothingMoved()
    {
        var path = ErrorFile("e.txt");
        File.WriteAllText(Markers, "not a directory");

        var result = Run(path);

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Equal(30, result.ExitCode);
        Assert.Empty(Files(Outbound));
        Assert.Empty(Files(Work));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("notes.csv")]
    [InlineData("e{1}.txt")]
    [InlineData("noextension")]
    public void InvalidErrorFileName_FailsBeforeAnyClaim(string name)
    {
        var path = ErrorFile(name);

        var result = Run(path);

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Empty(Files(Markers));
        Assert.Empty(Files(Outbound));
        Assert.Empty(Files(Work));
        Assert.True(File.Exists(path));
    }


    [Fact]
    public void ExistingFinalOutbound_IsNeverOverwritten_AndNothingIsClaimed()
    {
        var path = ErrorFile("e.txt");
        Directory.CreateDirectory(Outbound);
        var existing = Path.Combine(Outbound, "e.txt");
        File.WriteAllText(existing, "existing");

        var result = Run(path);

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Equal(30, result.ExitCode);
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.Empty(Files(Markers));
        Assert.Empty(Files(Work));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ExistingTimestampedArchiveDestination_FailsClosed_NeverOverwrites_KeepsMarkerAndOutbound()
    {
        var path = ErrorFile("e.txt");
        var before = Sha(path);
        Directory.CreateDirectory(Archive);
        var existing = Path.Combine(Archive, "e" + Stamp + ".txt");
        File.WriteAllText(existing, "older archive");

        var result = Run(path);

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Equal(30, result.ExitCode);
        Assert.Equal("older archive", File.ReadAllText(existing));
        Assert.Single(Files(Archive));
        Assert.True(File.Exists(MarkerPath("e")));
        Assert.Equal(Expected, Text(Assert.Single(Files(Outbound))));
        Assert.Equal(before, Sha(path));
    }

    [Fact]
    public void ArchiveTimestamp_IsTakenWhenTheArchivePathIsBuilt()
    {
        var path = ErrorFile("e.txt");
        var processor = Processor();
        var calls = new List<string>();
        processor.Submission.UtcNow = () => { calls.Add("clock"); return Now; };
        processor.Submission.TestHook = (stage, destination) => calls.Add(stage + ":" + Path.GetFileName(destination));

        Run(path, processor);

        Assert.Equal(["BeforeOutboundMove:e.txt", "AfterOutboundMove:e.txt", "clock", "BeforeArchiveMove:e" + Stamp + ".txt"], calls);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("removed")]
    public void DownstreamPickingUpTheOutboundFileImmediately_IsNotAFailure_AndTheOriginalIsArchived(string pickup)
    {
        var path = ErrorFile("e.txt");
        var before = Sha(path);
        var processor = Processor();
        FileStream? held = null;
        processor.Submission.TestHook = (stage, destination) =>
        {
            if (stage != "AfterOutboundMove") return;
            if (pickup == "locked") held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
            else File.Delete(destination);
        };

        ProcessingResult result;
        try { result = Run(path, processor); }
        finally { held?.Dispose(); }

        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutboundReleased);
        Assert.True(File.Exists(MarkerPath("e")));
        Assert.False(File.Exists(path));
        Assert.Equal(before, Sha(Assert.Single(Files(Archive))));
    }
    [Fact]
    public void OutboundDestinationAppearingBeforeTheMove_IsNotOverwritten_MarkerRetained_ErrorNotArchived()
    {
        var path = ErrorFile("e.txt");
        var final = Path.Combine(Outbound, "e.txt");
        var processor = Processor();
        processor.Submission.TestHook = (stage, _) =>
        {
            if (stage == "BeforeOutboundMove") File.WriteAllText(final, "someone else");
        };

        var result = Run(path, processor);

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Equal(30, result.ExitCode);
        Assert.Equal("someone else", File.ReadAllText(final));
        Assert.True(File.Exists(MarkerPath("e")));
        Assert.Single(Files(Markers));
        Assert.True(File.Exists(path));
        Assert.Empty(Files(Archive));
        Assert.Equal(Expected, Text(Assert.Single(Files(Work))));
        Assert.Equal(Files(Work)[0], result.RetryUciFile);
    }

    [Fact]
    public void ArchiveFailureAfterOutbound_KeepsMarkerAndOutbound_WithoutRollback_AndNeverOverwrites()
    {
        var path = ErrorFile("e.txt");
        var before = Sha(path);
        var archived = Path.Combine(Archive, "e" + Stamp + ".txt");
        var processor = Processor();
        processor.Submission.TestHook = (stage, _) =>
        {
            if (stage == "BeforeArchiveMove") File.WriteAllText(archived, "appeared later");
        };

        var result = Run(path, processor);

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Equal(30, result.ExitCode);
        Assert.Equal(Expected, Text(Assert.Single(Files(Outbound))));
        Assert.Equal(result.OutboundFile, Files(Outbound)[0]);
        Assert.True(File.Exists(MarkerPath("e")));
        Assert.Single(Files(Markers));
        Assert.Equal("appeared later", File.ReadAllText(archived));
        Assert.True(File.Exists(path));
        Assert.Equal(before, Sha(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"status\":\"Prepared\",\"originalFi")]
    [InlineData("{\"status\":\"Prepared\"}")]
    public void AnyExistingMarker_EmptyMalformedPartialOrComplete_BlocksTheRetry_AndIsLeftUntouched(string content)
    {
        Directory.CreateDirectory(Markers);
        File.WriteAllText(MarkerPath("e"), content);

        var result = Run(ErrorFile("e.txt"));

        Assert.Equal(ProcessingOutcome.AlreadyRetried, result.Outcome);
        Assert.Equal(content, File.ReadAllText(MarkerPath("e")));
        Assert.Single(Files(Markers));
        Assert.False(Directory.Exists(Outbound));
    }

    [Fact]
    public void SuccessfulRetry_LeavesMarkerStatusPrepared_WithoutCompanionFile()
    {
        var result = Run(ErrorFile("e.txt"));
        var after = File.ReadAllText(MarkerPath("e"));

        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, result.Outcome);
        Assert.Equal("Prepared", JsonDocument.Parse(after).RootElement.GetProperty("status").GetString());
        Assert.Equal(MarkerPath("e"), Assert.Single(Files(Markers)));
    }

    [Fact]
    public void MultiAssetGeoCodeError_CorrectsEveryAsSection_AndCompletesTheLifecycle()
    {
        var uci = UciBuilder.Join(
            UciBuilder.Cu, UciBuilder.Co,
            UciBuilder.As("365353706"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami,
            UciBuilder.As("36535-3706"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami,
            UciBuilder.As("36535-3706"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami,
            UciBuilder.Rn);
        var message = TestData.GeoMessage;
        var wrapper = "\r\nex10000001.txt\r\n " + message + "\u00FD 0\u00FD " + message.Replace("4444", "4445") + "\u00FD 0\u00FD "
            + message.Replace("4444", "4446") + "\u00FD 0\u00FD " + TestData.Identifier + " Contract not created.\r\n";
        var path = Path.Combine(_dir, "in", "ex10000001.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ErrorFileEncoding.Encoding.GetBytes(wrapper + uci + "{\r\n" + TestData.Identifier));

        var result = Run(path);

        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, result.Outcome);
        Assert.Equal(UciErrorType.GeoCode, result.ErrorType);
        Assert.Equal(3, result.AsSectionsInspected);
        Assert.Equal(3, result.EquipZipFieldsModified);
        Assert.Equal(3, Regex.Matches(Text(result.OutboundFile!), @"\|36535\|").Count);
        Assert.Equal(MarkerPath("ex10000001"), Assert.Single(Files(Markers)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SecondRunForTheSameFileName_IsBlockedByTheMarker()
    {
        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, Run(ErrorFile("e.txt")).Outcome);

        var again = Run(ErrorFile("e.txt"), Processor(Path.Combine(_dir, "out2"), Path.Combine(_dir, "arch2")));

        Assert.Equal(ProcessingOutcome.AlreadyRetried, again.Outcome);
        Assert.Single(Files(Outbound));
        Assert.Single(Files(Archive));
        Assert.False(Directory.Exists(Path.Combine(_dir, "out2")));
    }

    [Fact]
    public void NotEligible_CreatesNoMarkerOrMoves()
    {
        var path = ErrorFile("e.txt", UciBuilder.Valid("52302"));

        var result = Run(path);

        Assert.Equal(ProcessingOutcome.ManualReview, result.Outcome);
        Assert.False(Directory.Exists(Markers));
        Assert.False(Directory.Exists(Outbound));
        Assert.False(Directory.Exists(Archive));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ConcurrentRunsForOneErrorFileName_ClaimAndReleaseExactlyOnce()
    {
        const int n = 8;
        using var barrier = new Barrier(n);
        var tasks = Enumerable.Range(0, n).Select(i => Task.Factory.StartNew(() =>
        {
            // Same file name from different source folders; separate destinations so only the marker is contended.
            var path = ErrorFile("e.txt", sub: $"in{i}");
            var processor = Processor(Path.Combine(_dir, $"out{i}"), Path.Combine(_dir, $"arch{i}"));
            var analysis = processor.Analyze(path);
            barrier.SignalAndWait();
            return processor.ToResult(analysis, path).Outcome;
        }, TaskCreationOptions.LongRunning)).ToArray();

        var outcomes = await Task.WhenAll(tasks);

        Assert.Equal(1, outcomes.Count(o => o == ProcessingOutcome.AutoRetrySubmitted));
        Assert.Equal(n - 1, outcomes.Count(o => o == ProcessingOutcome.AlreadyRetried));
        Assert.Single(Enumerable.Range(0, n), i => File.Exists(Path.Combine(_dir, $"out{i}", "e.txt")));
        Assert.Single(Enumerable.Range(0, n), i => File.Exists(Path.Combine(_dir, $"arch{i}", "e" + Stamp + ".txt")));
        Assert.Single(Files(Markers));
        Assert.Empty(Files(Work));
    }

    [Fact]
    public async Task ConcurrentRunsForDifferentErrorFiles_AllSucceed()
    {
        const int n = 6;
        using var barrier = new Barrier(n);
        var paths = Enumerable.Range(0, n).Select(i => ErrorFile($"ex{i}.txt")).ToArray();
        var tasks = paths.Select(p => Task.Factory.StartNew(() =>
        {
            var processor = Processor();
            var analysis = processor.Analyze(p);
            barrier.SignalAndWait();
            return processor.ToResult(analysis, p).Outcome;
        }, TaskCreationOptions.LongRunning)).ToArray();

        var outcomes = await Task.WhenAll(tasks);

        Assert.All(outcomes, o => Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, o));
        Assert.Equal(n, Files(Outbound).Length);
        Assert.Equal(n, Files(Archive).Length);
        Assert.Empty(Files(Work));
    }

    [Fact]
    public void Run_LeaksNoPayloadOrZipValues_ToStdoutLogsMarkerOrStatus()
    {
        const string zip = "871234567";
        var path = ErrorFile("ex1.txt", UciBuilder.Valid(zip));
        var stdout = new StringWriter();
        var logs = new Capture();

        var exit = UciRetryApp.Run(["--error-file", path], stdout, logs, TestData.Configuration(Work));

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(Path.Combine(Outbound, "ex1.txt"), json.RootElement.GetProperty("outboundFile").GetString());
        Assert.Matches(@"^ex1_\d{8}_\d{6}\.txt$", Path.GetFileName(json.RootElement.GetProperty("archivedErrorFile").GetString()));
        var texts = logs.Messages.Append(stdout.ToString()).Append(File.ReadAllText(MarkerPath("ex1")));
        foreach (var text in texts)
        {
            Assert.DoesNotContain("CU|", text);
            Assert.DoesNotContain(zip, text);
            Assert.DoesNotContain("87123", text);
        }
    }

    [Theory]
    [InlineData("Processor:RetryMarkerDirectory")]
    [InlineData("Processor:ArchiveDirectory")]
    [InlineData("Processor:OutboundDirectory")]
    public void MissingDirectorySetting_IsProcessingError_AndNothingIsWritten(string key)
    {
        var path = ErrorFile("e.txt");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(TestData.Settings).AddInMemoryCollection(TestData.Directories(Work))
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "" }).Build();

        var exit = UciRetryApp.Run(["--error-file", path], new StringWriter(), NullLoggerFactory.Instance, config);

        Assert.Equal(50, exit);
        Assert.Empty(Files(Work));
        Assert.Empty(Files(Markers));
        Assert.Empty(Files(Outbound));
        Assert.True(File.Exists(path));
    }

    private sealed class Capture : ILoggerFactory
    {
        public List<string> Messages { get; } = [];
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }

        private sealed class Sink(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }
}
