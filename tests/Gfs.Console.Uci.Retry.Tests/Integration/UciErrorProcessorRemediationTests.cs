using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Processing;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Integration;
public sealed class UciErrorProcessorRemediationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WorkDir => TestData.WorkDir(_dir);

    private (UciErrorProcessor Processor, string Path) Setup(string uci, string after = "\r\n" + TestData.Identifier)
    {
        var (processor, path) = TestData.Create(TestData.GeoErrorFile(uci, after), _dir, out _);
        return (processor, path);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string[] WorkFiles => Directory.Exists(WorkDir) ? Directory.GetFiles(WorkDir) : [];
    private string[] OutFiles => Directory.Exists(Path.Combine(_dir, "outbound")) ? Directory.GetFiles(Path.Combine(_dir, "outbound")) : [];

    [Fact]
    public void Success_WritesExactlyTheCorrectedUciToANewWorkingFile_AndLeavesErrorFileUntouched()
    {
        var uci = UciBuilder.Valid("523021234", "12345", "52302-1234", "") + "|";
        var (processor, path) = Setup(uci);
        var before = Sha(path);

        var analysis = processor.Analyze(path);
        var original = analysis.EmbeddedUci!.Text;
        var result = processor.ToResult(analysis, path);

        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(UciErrorType.GeoCode, result.ErrorType);
        Assert.Equal(TestData.Identifier, result.UciIdentifier);
        Assert.Equal(4, result.AsSectionsInspected);
        Assert.Equal(2, result.EquipZipFieldsModified);

        // The working file was moved (once) to the outbound directory under the error file's name.
        Assert.Empty(WorkFiles);
        var file = Assert.Single(OutFiles);
        Assert.Equal(file, result.OutboundFile);
        Assert.Equal("e.txt", Path.GetFileName(file));
        var written = ErrorFileEncoding.Encoding.GetString(File.ReadAllBytes(file));
        Assert.Equal(UciBuilder.Valid("52302", "12345", "52302", "") + "|", written);
        Assert.DoesNotContain('{', written);

        // The original error file was archived unchanged.
        Assert.False(File.Exists(path));
        Assert.Equal(before, Sha(Assert.Single(Directory.GetFiles(Path.Combine(_dir, "archive"), "e_*.txt"))));
        Assert.Equal(uci, original);
        Assert.Equal(uci, analysis.EmbeddedUci.Text);
        Assert.Equal(uci, analysis.Document!.Source);
    }

    [Fact]
    public void NonAsciiBytesAndLineEndings_SurviveInTheWorkingFileByteForByte()
    {
        var uci = UciBuilder.Valid("523021234").Replace("|Item|", "|\u00FD\u00E9\r\n\u0080|").Replace("|AUC|", "|\r\nAUC|");
        var (processor, path) = Setup(uci);

        var result = processor.ToResult(processor.Analyze(path), path);

        Assert.Equal(1, result.EquipZipFieldsModified);
        var expected = ErrorFileEncoding.Encoding.GetBytes(uci.Replace("523021234", "52302"));
        Assert.Equal(expected, File.ReadAllBytes(Assert.Single(OutFiles)));
    }

    [Fact]
    public void ErrorFileWithBomAndWrapper_ProducesWorkingFileWithoutEitherWrapperPart()
    {
        var uci = UciBuilder.Valid("523021234");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(ErrorFileEncoding.Encoding.GetBytes(TestData.GeoErrorFile(uci).TrimStart('\r', '\n').Insert(0, "\r\n"))).ToArray();
        var path = Path.Combine(_dir, "bom.txt");
        File.WriteAllBytes(path, bytes);
        var processor = new UciErrorProcessor(TestData.Options(WorkDir), NullLogger.Instance);

        var result = processor.ToResult(processor.Analyze(path), path);

        Assert.Equal(1, result.EquipZipFieldsModified);
        var written = File.ReadAllBytes(Assert.Single(OutFiles));
        Assert.Equal(ErrorFileEncoding.Encoding.GetBytes(UciBuilder.Valid("52302")), written);
    }

    [Theory]
    [InlineData("alreadyFive")]
    [InlineData("blank")]
    [InlineData("unsupported")]
    [InlineData("validPlusUnsupported")]
    public void NotEligible_IsManualReview_WithNoWorkingFile(string scenario)
    {
        var uci = scenario switch
        {
            "alreadyFive" => UciBuilder.Valid("52302"),
            "blank" => UciBuilder.Valid(""),
            "unsupported" => UciBuilder.Valid("5230212"),
            _ => UciBuilder.Valid("523021234", "ABCDE"),
        };
        var (processor, path) = Setup(uci);
        var before = Sha(path);

        var result = processor.ToResult(processor.Analyze(path), path);

        Assert.Equal(ProcessingOutcome.ManualReview, result.Outcome);
        Assert.Equal(UciErrorType.GeoCode, result.ErrorType);
        Assert.Equal(TestData.Identifier, result.UciIdentifier);
        Assert.NotNull(result.AsSectionsInspected);
        Assert.Null(result.EquipZipFieldsModified);
        Assert.Null(result.RetryUciFile);
        Assert.Empty(WorkFiles);
        Assert.False(Directory.Exists(WorkDir));
        Assert.Equal(before, Sha(path));
    }

    [Fact]
    public void WorkingDirectoryThatCannotBeUsed_IsRemediationFailed_AndNothingIsSubmitted()
    {
        var (_, path) = Setup(UciBuilder.Valid("523021234"));
        var blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var processor = new UciErrorProcessor(TestData.Options(blocker), NullLogger.Instance);
        var before = Sha(path);

        var result = processor.ToResult(processor.Analyze(path), path);

        Assert.Equal(ProcessingOutcome.RemediationFailed, result.Outcome);
        Assert.Equal(30, result.ExitCode);
        Assert.Equal(UciErrorType.GeoCode, result.ErrorType);
        Assert.Equal(TestData.Identifier, result.UciIdentifier);
        Assert.Null(result.RetryUciFile);
        Assert.Null(result.OutboundFile);
        Assert.Equal(before, Sha(path));
    }

    [Fact]
    public void MissingWorkingDirectoryConfiguration_IsProcessingError()
    {
        TestData.Create(TestData.GeoErrorFile(UciBuilder.Valid("523021234")), _dir, out var path);
        var exit = UciRetryApp.Run(["--error-file", path], new StringWriter(), NullLoggerFactory.Instance, TestData.Configuration());
        Assert.Equal(50, exit);
    }

    [Fact]
    public void EndToEnd_Success_PrintsOneJsonObjectWithCountsAndNoPayloadOrZipInStdoutOrLogs()
    {
        const string zip = "871234567";
        TestData.Create(TestData.GeoErrorFile(UciBuilder.Valid(zip, "52302")), _dir, out var path);
        var stdout = new StringWriter();
        var logs = new Capture();

        var exit = UciRetryApp.Run(["--error-file", path], stdout, logs, TestData.Configuration(WorkDir));

        using var json = JsonDocument.Parse(stdout.ToString());
        var root = json.RootElement;
        Assert.Equal(0, exit);
        Assert.Equal("AutoRetrySubmitted", root.GetProperty("outcome").GetString());
        Assert.Equal("GeoCode", root.GetProperty("errorType").GetString());
        Assert.Equal(TestData.Identifier, root.GetProperty("uciIdentifier").GetString());
        Assert.Equal(2, root.GetProperty("asSectionsInspected").GetInt32());
        Assert.Equal(1, root.GetProperty("equipZipFieldsModified").GetInt32());
        Assert.Equal(Assert.Single(OutFiles), root.GetProperty("outboundFile").GetString());
        Assert.True(File.Exists(root.GetProperty("outboundFile").GetString()));
        Assert.True(File.Exists(root.GetProperty("markerFile").GetString()));

        foreach (var text in logs.Messages.Append(stdout.ToString()))
        {
            Assert.DoesNotContain("CU|", text);
            Assert.DoesNotContain(zip, text);
            Assert.DoesNotContain("87123", text);
        }
    }

    [Fact]
    public void EndToEnd_Failure_ReturnsExit30()
    {
        var (_, path) = Setup(UciBuilder.Valid("523021234"));
        var blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "x");
        var stdout = new StringWriter();

        var exit = UciRetryApp.Run(["--error-file", path], stdout, NullLoggerFactory.Instance, TestData.Configuration(blocker));

        Assert.Equal(30, exit);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("RemediationFailed", json.RootElement.GetProperty("outcome").GetString());
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

    [Theory]
    [InlineData("contractFirst")]
    [InlineData("contractMiddleUnpaired")]
    public void ReorderedWrapper_IsGeoCode_AndEveryAsSectionIsRemediated(string layout)
    {
        var msg = TestData.GeoMessage;
        var wrapper = layout == "contractFirst"
            ? "0\u00FD " + TestData.Identifier + " Contract not created.\u00FD " + msg
            : msg + "\u00FD 0\u00FD " + msg + "\u00FD 0\u00FD 0\u00FD " + TestData.Identifier + " Contract not created.\u00FD " + msg;
        var uci = UciBuilder.Valid("523021234", "52302-1234", "12345");
        TestData.Create("\r\nex1.txt\r\n " + wrapper + "\r\n" + uci + "{\r\n" + TestData.Identifier, _dir, out var path);
        var processor = new UciErrorProcessor(TestData.Options(WorkDir), NullLogger.Instance);

        var result = processor.ToResult(processor.Analyze(path), path);

        Assert.Equal(ProcessingOutcome.AutoRetrySubmitted, result.Outcome);
        Assert.Equal(UciErrorType.GeoCode, result.ErrorType);
        Assert.Equal(3, result.AsSectionsInspected);
        Assert.Equal(2, result.EquipZipFieldsModified);
    }
}