using Gfs.Console.Uci.Retry.Processing;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Processing;

public sealed class UciSubmissionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 11, 56, 22, DateTimeKind.Utc);

    [Theory]
    [InlineData("C:\\x\\..")]
    [InlineData("C:\\x\\.")]
    [InlineData("C:\\x\\")]
    [InlineData("")]
    public void TraversalAndDirectoryOnlyPaths_AreRejected(string errorFile)
    {
        var options = TestData.Options(Path.Combine(Path.GetTempPath(), "uciretry-unused", "work"));
        var service = new UciSubmissionService(options, NullLogger.Instance);
        Assert.False(service.TryPlan(errorFile, out var plan, out var failure));
        Assert.Null(plan);
        Assert.NotNull(failure);
    }

    [Theory]
    [InlineData("ex10000001.txt", "ex10000001_20261030_115622.txt")]
    [InlineData("a.b.txt", "a.b_20261030_115622.txt")]
    [InlineData("UPPER.TXT", "UPPER_20261030_115622.TXT")]
    public void ArchiveFileName_KeepsBaseNameAndExtension_AndAppendsTheTimestamp(string name, string expected)
        => Assert.Equal(expected, UciSubmissionService.ArchiveFileName(name, Now));
}

public sealed class UciSubmissionServiceArchiveHandledTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uciretry-archive-").FullName;
    private static readonly DateTime Now = new(2026, 11, 3, 13, 11, 0, DateTimeKind.Utc);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private UciSubmissionService Service() =>
        new(TestData.Options(Path.Combine(_dir, "work")), NullLogger.Instance) { UtcNow = () => Now };

    private string ErrorFile(string name = "ex10000001.txt", string content = "synthetic 900-1234567-000")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Archives_UnderTimestampedOriginalName_WithIdenticalBytes_AndRemovesSource()
    {
        var path = ErrorFile();
        var original = File.ReadAllBytes(path);

        var result = Service().ArchiveHandledError(path);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_dir, "archive", "ex10000001_20261103_131100.txt"), result.Path);
        Assert.Equal(original, File.ReadAllBytes(result.Path!));
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("1234567", Path.GetFileName(result.Path!)); // never the contract/UCI identifier
    }

    [Fact]
    public void ExistingArchiveDestination_IsNotOverwritten_AndSourceRemains()
    {
        var path = ErrorFile();
        Directory.CreateDirectory(Path.Combine(_dir, "archive"));
        var existing = Path.Combine(_dir, "archive", "ex10000001_20261103_131100.txt");
        File.WriteAllText(existing, "keep me");

        var result = Service().ArchiveHandledError(path);

        Assert.False(result.Success);
        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void BlankArchiveDirectory_Throws()
    {
        var options = TestData.Options(Path.Combine(_dir, "work"));
        options.ArchiveDirectory = "";
        Assert.Throws<InvalidOperationException>(() => new UciSubmissionService(options, NullLogger.Instance).ArchiveHandledError(ErrorFile()));
    }
}