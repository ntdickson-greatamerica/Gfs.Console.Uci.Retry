using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Processing;
public sealed class RetryTrackerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("uciretry-").FullName;
    private string MarkerDir => Path.Combine(_root, "markers");

    public void Dispose() => Directory.Delete(_root, true);

    private RetryTracker Tracker(string? dir = null) =>
        new(new ProcessorOptions { RetryMarkerDirectory = dir ?? MarkerDir }, NullLogger.Instance);

    private static RetryMarker Marker(string name = "ex100.txt") => new()
    {
        OriginalFileName = name,
        UciIdentifier = "100-2000000-300",
        ErrorType = UciErrorType.GeoCode,
        WorkingFile = "work.txt",
        AsSectionsInspected = 3,
        EquipZipFieldsModified = 2,
        CreatedUtc = DateTimeOffset.UtcNow
    };

    private string[] Files() => Directory.Exists(MarkerDir) ? Directory.GetFiles(MarkerDir) : [];

    [Fact]
    public void FirstClaimSucceedsAndSecondIsAlreadyRetried()
    {
        var t = Tracker();
        var first = t.TryClaim(Marker());
        Assert.Equal(RetryClaimStatus.Claimed, first.Status);
        Assert.Equal(Path.Combine(MarkerDir, "ex100.geo-retry.json"), first.MarkerFile);
        Assert.Equal(RetryClaimStatus.AlreadyRetried, t.TryClaim(Marker()).Status);
        Assert.Single(Files());
    }

    [Fact]
    public void MissingDirectoryIsCreated()
    {
        Assert.False(Directory.Exists(MarkerDir));
        Assert.Equal(RetryClaimStatus.Claimed, Tracker().TryClaim(Marker()).Status);
        Assert.True(Directory.Exists(MarkerDir));
    }

    [Fact]
    public async Task ParallelClaimsForOneIdentifierYieldExactlyOneClaim()
    {
        const int n = 16;
        var t = Tracker();
        using var barrier = new Barrier(n);
        var results = Enumerable.Range(0, n).Select(_ => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            return t.TryClaim(Marker()).Status;
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(results);

        Assert.Equal(1, results.Count(r => r.Result == RetryClaimStatus.Claimed));
        Assert.Equal(n - 1, results.Count(r => r.Result == RetryClaimStatus.AlreadyRetried));
        Assert.Single(Files());
    }

    [Fact]
    public async Task ParallelClaimsForDifferentIdentifiersAllSucceed()
    {
        const int n = 16;
        var t = Tracker();
        using var barrier = new Barrier(n);
        var results = Enumerable.Range(0, n).Select(i => Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            return t.TryClaim(Marker($"ex{i:000}.txt")).Status;
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(results);

        Assert.All(results, r => Assert.Equal(RetryClaimStatus.Claimed, r.Result));
        Assert.Equal(n, Files().Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"uciIdentifier\":\"100-2000000-300\",\"status\":\"Submitted\"}")]
    [InlineData("not json at all")]
    [InlineData("{\"uciIdentifier\":\"100-20")]
    public void ExistingMarkerOfAnyContentIsAlreadyRetriedAndUntouched(string content)
    {
        Directory.CreateDirectory(MarkerDir);
        var path = Path.Combine(MarkerDir, "ex100.geo-retry.json");
        File.WriteAllText(path, content);
        var before = File.GetLastWriteTimeUtc(path);

        var result = Tracker().TryClaim(Marker());

        Assert.Equal(RetryClaimStatus.AlreadyRetried, result.Status);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Single(Files());
    }

    [Fact]
    public void MarkerDirectoryThatIsAFileFailsInsteadOfAlreadyRetried()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var result = Tracker(blocker).TryClaim(Marker());
        Assert.Equal(RetryClaimStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankMarkerDirectoryThrowsConfigurationError(string dir)
    {
        Assert.Throws<InvalidOperationException>(() => Tracker(dir).TryClaim(Marker()));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("a/b.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("\\evil.txt")]
    [InlineData("a..b.txt")]
    [InlineData(".txt")]
    [InlineData(".hidden.txt")]
    [InlineData(" a.txt")]
    [InlineData("")]
    public void UnsafeFileNamesAreRejectedWithoutCreatingFiles(string name)
    {
        var result = Tracker().TryClaim(Marker(name));
        Assert.Equal(RetryClaimStatus.Failed, result.Status);
        Assert.Empty(Files());
        Assert.False(File.Exists(Path.Combine(_root, "evil.geo-retry.json")));
    }

    [Fact]
    public void MarkerFileNameIsDeterministic()
    {
        var a = RetryTracker.GetMarkerPath(MarkerDir, "ex10000001.txt");
        var b = RetryTracker.GetMarkerPath(MarkerDir, "ex10000001.txt");
        Assert.Equal(a, b);
        Assert.Equal("ex10000001.geo-retry.json", Path.GetFileName(a));
        Assert.Equal(Path.GetFullPath(MarkerDir), Path.GetDirectoryName(a));
    }

    [Fact]
    public void MarkerJsonHasDiagnosticFieldsOnly()
    {
        var result = Tracker().TryClaim(Marker());
        var json = File.ReadAllText(result.MarkerFile!);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("ex100.txt", root.GetProperty("originalFileName").GetString());
        Assert.Equal("100-2000000-300", root.GetProperty("uciIdentifier").GetString());
        Assert.Equal("GeoCode", root.GetProperty("errorType").GetString());
        Assert.Equal("Prepared", root.GetProperty("status").GetString());
        Assert.Equal("work.txt", root.GetProperty("workingFile").GetString());
        Assert.Equal(3, root.GetProperty("asSectionsInspected").GetInt32());
        Assert.Equal(2, root.GetProperty("equipZipFieldsModified").GetInt32());
        Assert.True(root.GetProperty("createdUtc").GetDateTimeOffset().Offset == TimeSpan.Zero
            || json.Contains("Z\"", StringComparison.Ordinal) || json.Contains("+00:00", StringComparison.Ordinal));
        Assert.Equal(8, root.EnumerateObject().Count());
    }
}
