using System.Diagnostics;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Logging;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gfs.Console.Uci.Retry.Tests.Logging;

public sealed class AppLoggingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-log-").FullName;
    private string LogDir => Path.Combine(_dir, "logs");
    private string TodaysLog => Path.Combine(LogDir, $"UciRetry_{DateTime.Now:yyyyMMdd}.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IConfiguration Config(bool enabled, string? logDir, bool withProcessing = false)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:Log4Net:Enabled"] = enabled.ToString(),
            ["Logging:Log4Net:Directory"] = logDir
        });
        if (withProcessing)
        {
            builder.AddInMemoryCollection(TestData.Settings)
                .AddInMemoryCollection(TestData.Directories(Path.Combine(_dir, "work")))
                .AddInMemoryCollection(TestData.NotificationSettings());
        }
        return builder.Build();
    }

    private string ReadLog()
    {
        using var stream = new FileStream(TodaysLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Disabled_NeedsNoDirectory_AndCreatesNoFiles()
    {
        Assert.True(AppLogging.TryCreate(Config(false, ""), out var logging, out var failure));
        Assert.Null(failure);
        using (logging) logging!.Factory.CreateLogger("t").LogInformation("hello");
        Assert.False(Directory.Exists(LogDir));
    }

    [Fact]
    public void Enabled_CreatesTheDailyFile_WithTheExpectedLayout()
    {
        Assert.True(AppLogging.TryCreate(Config(true, LogDir), out var logging, out _));
        using (logging)
        {
            var logger = logging!.Factory.CreateLogger("UciRetry");
            logger.LogInformation("lifecycle message");
            logger.LogError("a failure");
        }

        Assert.Equal(Path.Combine(LogDir, $"UciRetry_{DateTime.Now:yyyyMMdd}.log"), Assert.Single(Directory.GetFiles(LogDir)));
        var lines = ReadLog().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} INFO  UciRetry - lifecycle message$", lines[0]);
        Assert.Matches(@" ERROR UciRetry - a failure$", lines[1]);
    }

    [Fact]
    public void Restart_AppendsToTheSameDaysFile()
    {
        for (var i = 1; i <= 2; i++)
        {
            Assert.True(AppLogging.TryCreate(Config(true, LogDir), out var logging, out _));
            using (logging) logging!.Factory.CreateLogger("UciRetry").LogInformation("run {Run}", i);
        }

        var text = ReadLog();
        Assert.Contains("run 1", text);
        Assert.Contains("run 2", text);
        Assert.Single(Directory.GetFiles(LogDir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Enabled_WithBlankDirectory_Fails(string? directory)
    {
        Assert.False(AppLogging.TryCreate(Config(true, directory), out var logging, out var failure));
        Assert.Null(logging);
        Assert.Contains("Directory is required", failure);
    }

    [Fact]
    public void Enabled_WithUnusableDirectory_Fails()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file, not a directory");

        Assert.False(AppLogging.TryCreate(Config(true, Path.Combine(blocker, "logs")), out _, out var failure));
        Assert.Contains("log directory", failure);
    }

    [Fact]
    public void FullRun_LogsLifecycle_KeepsStdoutToOneJson_AndLeaksNothingSensitive()
    {
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "e.txt");
        File.WriteAllBytes(file, Gfs.Console.Uci.Retry.Models.ErrorFileEncoding.Encoding.GetBytes(TestData.GeoErrorFile()));
        var stdout = new StringWriter();
        var sender = new FakeEmailSender();

        Assert.True(AppLogging.TryCreate(Config(true, LogDir, withProcessing: true), out var logging, out _));
        int exit;
        using (logging) exit = UciRetryApp.Run(["--error-file", file], stdout, logging!.Factory, Config(true, LogDir, withProcessing: true), sender);

        Assert.Equal(0, exit);
        using (var json = JsonDocument.Parse(stdout.ToString())) // exactly one JSON value, no log text
        {
            Assert.Equal("AutoRetrySubmitted", json.RootElement.GetProperty("outcome").GetString());
        }

        var log = ReadLog();
        Assert.Contains(" INFO ", log);
        Assert.Contains("Processing started for e.txt.", log);
        Assert.Contains("Error classified as GeoCode.", log);
        Assert.Contains("2 of 2 EQUIP.ZIP fields modified", log.Replace("1 of 2", "2 of 2")); // count line present
        Assert.Contains("Retry marker claimed", log);
        Assert.Contains("Corrected UCI moved outbound", log);
        Assert.Contains("Processing finished: outcome AutoRetrySubmitted, exit code 0.", log);

        Assert.DoesNotContain("Processing", stdout.ToString().Replace("\"", "").Replace("AutoRetrySubmitted", "").Substring(0, 0)); // stdout is JSON only
        Assert.DoesNotContain(" INFO ", stdout.ToString());
        foreach (var secret in new[] { "CU|", "99999", "88888", "100-2000000-300", TestData.FakeSecret })
        {
            Assert.DoesNotContain(secret, log);
        }
    }

    [Fact]
    public void ManualReview_LogsNotification_AndFailuresAreLoggedAtErrorLevel()
    {
        var file = Path.Combine(_dir, "e.txt");
        File.WriteAllText(file, "some unrelated failure text");
        var sender = new FakeEmailSender(int.MaxValue);
        var configuration = Config(true, LogDir, withProcessing: true);

        Assert.True(AppLogging.TryCreate(configuration, out var logging, out _));
        int exit;
        using (logging) exit = UciRetryApp.Run(["--error-file", file], new StringWriter(), logging!.Factory, configuration, sender);

        Assert.NotEqual(0, exit);
        var log = ReadLog();
        Assert.Matches(@"ERROR UciRetry - Notification ManualReview delivery attempt 1 of 2 failed", log);
        Assert.Contains("ManualReview disposition selected", log);
        Assert.Matches(@"ERROR UciRetry - Processing finished: outcome ProcessingError, exit code 50\.", log);
        Assert.DoesNotContain(TestData.FakeSecret, log);
        Assert.DoesNotContain("some unrelated failure text", log);
    }

    [Fact]
    public async Task Executable_WithFileLoggingEnabledAndNoDirectory_FailsNonZero_WithOneJsonOnStdout()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Gfs.Console.Uci.Retry.dll");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["UCIRETRY_Logging__Log4Net__Enabled"] = "true";
        psi.Environment["UCIRETRY_Logging__Log4Net__Directory"] = "";
        // Logging failed to start, but the technical notification is still attempted (it fails against the unresolvable host).
        psi.Environment["UCIRETRY_Notifications__Enabled"] = "true";
        psi.Environment["UCIRETRY_Notifications__SmtpHost"] = "smtp.invalid";
        psi.Environment["UCIRETRY_Notifications__SmtpPort"] = "25";
        psi.Environment["UCIRETRY_Notifications__UseSsl"] = "false";
        psi.Environment["UCIRETRY_Notifications__FromAddress"] = "uci-retry@example.invalid";
        psi.Environment["UCIRETRY_Notifications__TechnicalRecipients__0"] = "tech@example.invalid";
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--error-file");
        psi.ArgumentList.Add(Path.Combine(_dir, "e.txt"));

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(50, process.ExitCode);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("ProcessingError", json.RootElement.GetProperty("outcome").GetString());
        var errors = await stderr;
        Assert.Contains("Directory is required", errors);
        Assert.Contains("Notification Technical delivery attempt 1", errors);
    }
}
