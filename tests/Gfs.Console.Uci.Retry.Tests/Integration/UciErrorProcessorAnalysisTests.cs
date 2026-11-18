using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Integration;
public sealed class UciErrorProcessorAnalysisTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GeoCodeWithUciAndIdentifier_IsReadyAndPreservesUci()
    {
        var (processor, path) = TestData.Create(TestData.GeoErrorFile(), _dir, out _);
        var before = File.ReadAllBytes(path);

        var analysis = processor.Analyze(path);

        Assert.True(analysis.IsReadyForRemediation);
        Assert.Equal(UciErrorType.GeoCode, analysis.ErrorType);
        Assert.Equal(TestData.Identifier, analysis.UciIdentifier);
        Assert.Equal(TestData.Uci, analysis.EmbeddedUci!.Text);
        Assert.Equal(2, analysis.Document!.AssetGroups.Count);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("noUci")]
    [InlineData("ambiguous")]
    [InlineData("noId")]
    [InlineData("conflictingId")]
    public void GeoCodeButUnsafe_IsManualReviewWithGeoCodeType(string scenario)
    {
        var content = scenario switch
        {
            "noUci" => "\r\n " + TestData.GeoMessage + " " + TestData.Identifier + " Contract not created.\r\n",
            "ambiguous" => TestData.GeoErrorFile(TestData.Uci + "{\r\n" + TestData.Uci),
            "noId" => TestData.GeoErrorFile().Replace(TestData.Identifier, "none"),
            _ => TestData.GeoErrorFile(after: "\r\n100-2000000-999"),
        };
        var (processor, path) = TestData.Create(content, _dir, out _);

        var analysis = processor.Analyze(path);
        var result = processor.ToResult(analysis, path);

        Assert.False(analysis.IsReadyForRemediation);
        Assert.Equal(ProcessingOutcome.ManualReview, result.Outcome);
        Assert.Equal(UciErrorType.GeoCode, result.ErrorType);
        Assert.Null(result.UciIdentifier);
    }

    [Fact]
    public void UnrelatedErrorWithUci_IsUnknown()
    {
        var (processor, path) = TestData.Create("\r\nSome other failure " + TestData.Identifier + "\r\n" + TestData.Uci + "{", _dir, out _);
        Assert.Equal(UciErrorType.Unknown, processor.Analyze(path).ErrorType);
    }

    [Fact]
    public void GeoMessageInsideUci_IsNotClassifiedFromPayload()
    {
        var (processor, path) = TestData.Create("\r\nOther failure\r\nCU|" + TestData.GeoMessage + "|RN||{", _dir, out _);
        Assert.Equal(UciErrorType.Unknown, processor.Analyze(path).ErrorType);
    }

    [Theory]
    [InlineData("missingCo")]
    [InlineData("noAsSections")]
    public void GeoCodeWithInvalidUci_IsManualReviewKeepingIdentifier(string scenario)
    {
        var uci = scenario == "missingCo"
            ? UciBuilder.Join(UciBuilder.Cu, UciBuilder.As("99999"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami, UciBuilder.Rn)
            : UciBuilder.Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.Rn);
        var (processor, path) = TestData.Create(TestData.GeoErrorFile(uci), _dir, out _);

        var result = processor.ToResult(processor.Analyze(path), path);

        Assert.Equal(ProcessingOutcome.ManualReview, result.Outcome);
        Assert.Equal(UciErrorType.GeoCode, result.ErrorType);
        Assert.Equal(TestData.Identifier, result.UciIdentifier);
        Assert.Null(result.AsSectionsInspected);
        Assert.DoesNotContain("remediation is not implemented", result.Message);
    }

    [Fact]
    public void EndToEnd_GeoCode_ReturnsAutoRetrySubmittedWithoutPayloadInStdout()
    {
        TestData.Create(TestData.GeoErrorFile(), _dir, out var path);
        var stdout = new StringWriter();
        var sink = new CapturingLoggerFactory();

        var exit = UciRetryApp.Run(["--error-file", path], stdout, sink, TestData.Configuration(TestData.WorkDir(_dir)));

        using var doc = JsonDocument.Parse(stdout.ToString());
        var json = doc.RootElement;
        Assert.Equal(0, exit);
        Assert.Equal("AutoRetrySubmitted", json.GetProperty("outcome").GetString());
        Assert.Equal("GeoCode", json.GetProperty("errorType").GetString());
        Assert.Equal(TestData.Identifier, json.GetProperty("uciIdentifier").GetString());
        Assert.Equal("Corrected UCI was released for retry and the original error file was archived.", json.GetProperty("message").GetString());
        Assert.Equal(2, json.GetProperty("asSectionsInspected").GetInt32());
        Assert.Equal(1, json.GetProperty("equipZipFieldsModified").GetInt32());
        Assert.DoesNotContain("CU|", stdout.ToString());
        Assert.All(sink.Messages, m => Assert.DoesNotContain("CU|", m));
    }

    [Fact]
    public void MissingConfiguration_IsProcessingError()
    {
        TestData.Create(TestData.GeoErrorFile(), _dir, out var path);
        var stdout = new StringWriter();
        var exit = UciRetryApp.Run(["--error-file", path], stdout, NullLoggerFactory.Instance, new ConfigurationBuilder().Build());
        Assert.Equal(50, exit);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = [];
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }

        private sealed class Capture(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }
}
