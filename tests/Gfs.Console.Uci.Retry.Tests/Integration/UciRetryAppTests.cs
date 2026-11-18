using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Integration;
public sealed class UciRetryAppTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CreateFile()
    {
        var path = Path.Combine(_dir, "error.txt");
        File.WriteAllText(path, "synthetic");
        return path;
    }

    private (int ExitCode, string Stdout) Run(string[] args, ILoggerFactory? loggerFactory = null)
    {
        var stdout = new StringWriter();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(TestData.Settings)
            .AddInMemoryCollection(TestData.Directories(TestData.WorkDir(_dir)))
            .AddInMemoryCollection(TestData.NotificationSettings()).Build();
        var exit = UciRetryApp.Run(args, stdout, loggerFactory ?? LoggerFactory.Create(_ => { }), configuration, new FakeEmailSender());
        return (exit, stdout.ToString());
    }

    private static JsonElement ParseSingle(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout); // throws if more than one JSON value or trailing text
        return doc.RootElement.Clone();
    }

    [Fact]
    public void MissingArgument_IsInvalidInput()
    {
        var (exit, stdout) = Run([]);
        Assert.Equal(40, exit);
        Assert.Equal("InvalidInput", ParseSingle(stdout).GetProperty("outcome").GetString());
    }

    [Fact]
    public void DuplicateArgument_IsInvalidInput()
    {
        var file = CreateFile();
        var (exit, _) = Run(["--error-file", file, "--error-file", file]);
        Assert.Equal(40, exit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyArgument_IsInvalidInput(string value)
    {
        var (exit, _) = Run(["--error-file", value]);
        Assert.Equal(40, exit);
    }

    [Fact]
    public void ArgumentWithoutValue_IsInvalidInput()
    {
        var (exit, _) = Run(["--error-file"]);
        Assert.Equal(40, exit);
    }

    [Fact]
    public void RelativePath_IsInvalidInput()
    {
        CreateFile();
        var (exit, _) = Run(["--error-file", "error.txt"]);
        Assert.Equal(40, exit);
    }

    [Fact]
    public void Directory_IsInvalidInput()
    {
        var (exit, stdout) = Run(["--error-file", _dir]);
        Assert.Equal(40, exit);
        Assert.Contains("directory", ParseSingle(stdout).GetProperty("message").GetString());
    }

    [Fact]
    public void NonexistentFile_IsInvalidInput()
    {
        var (exit, _) = Run(["--error-file", Path.Combine(_dir, "missing.txt")]);
        Assert.Equal(40, exit);
    }

    [Fact]
    public void ValidFile_WithUnsupportedContent_IsManualReviewUnknown()
    {
        var file = CreateFile();
        var (exit, stdout) = Run(["--error-file", file]);
        var json = ParseSingle(stdout);

        Assert.Equal(0, exit); // a delivered manual-review notification means the file was handled
        Assert.Equal("ManualReview", json.GetProperty("outcome").GetString());
        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Equal("Unknown", json.GetProperty("errorType").GetString());
        Assert.Equal(file, json.GetProperty("originalErrorFile").GetString());
        Assert.False(File.Exists(file)); // handled files are archived
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "archive")));
        Assert.False(json.TryGetProperty("uciIdentifier", out _));
    }

    [Theory]
    [InlineData(ProcessingOutcome.AutoRetrySubmitted, 0)]
    [InlineData(ProcessingOutcome.ManualReview, 0)]
    [InlineData(ProcessingOutcome.AlreadyRetried, 0)]
    [InlineData(ProcessingOutcome.RemediationFailed, 30)]
    [InlineData(ProcessingOutcome.InvalidInput, 40)]
    [InlineData(ProcessingOutcome.ProcessingError, 50)]
    public void ExitCodeMapping(ProcessingOutcome outcome, int expected)
    {
        Assert.Equal(expected, outcome.ToExitCode());
        var result = new ProcessingResult { Outcome = outcome, Message = "m" };
        Assert.Equal(expected, result.ExitCode);
        Assert.Equal(expected, ParseSingle(result.ToJson()).GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Result_OmitsUnknownValues()
    {
        var json = ParseSingle(new ProcessingResult { Outcome = ProcessingOutcome.ManualReview, Message = "m" }.ToJson());
        Assert.False(json.TryGetProperty("markerFile", out _));
        Assert.False(json.TryGetProperty("asSectionsInspected", out _));
    }

    [Fact]
    public void Logging_DoesNotContaminateStdout()
    {
        var file = CreateFile();
        var sink = new ListLoggerFactory();
        var (_, stdout) = Run(["--error-file", file], sink);

        Assert.NotEmpty(sink.Messages); // the app did log...
        ParseSingle(stdout);            // ...yet stdout is still exactly one JSON object
        Assert.All(sink.Messages, m => Assert.DoesNotContain(m, stdout));
    }

    [Fact]
    public async Task Executable_WritesJsonToStdoutAndLogsToStderr()
    {
        var file = CreateFile();
        var dll = Path.Combine(AppContext.BaseDirectory, "Gfs.Console.Uci.Retry.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Directories are configured so the failure under test is the undeliverable (disabled) notification, not missing configuration.
        // Never send real mail from a test, whatever the checked-in configuration says.
        psi.Environment["UCIRETRY_Notifications__Enabled"] = "false";
        psi.Environment["UCIRETRY_Processor__ArchiveDirectory"] = Path.Combine(_dir, "archive");
        psi.Environment["UCIRETRY_Processor__RetryWorkingDirectory"] = Path.Combine(_dir, "work");
        psi.Environment["UCIRETRY_Processor__RetryMarkerDirectory"] = Path.Combine(_dir, "markers");
        psi.Environment["UCIRETRY_Processor__OutboundDirectory"] = Path.Combine(_dir, "outbound");
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--error-file");
        psi.ArgumentList.Add(file);

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;

        // With notifications disabled, a manual-review outcome cannot be announced and fails safe.
        Assert.Equal(50, process.ExitCode);
        Assert.Equal("ProcessingError", ParseSingle(stdout).GetProperty("outcome").GetString());
        Assert.Contains("not a supported error type", stderr);
        Assert.Contains("notifications are disabled", stderr);
        Assert.True(File.Exists(file)); // undelivered notification: the file is not archived
    }

    private sealed class ListLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = [];
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new ListLogger(Messages);
        public void Dispose() { }

        private sealed class ListLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }
}
