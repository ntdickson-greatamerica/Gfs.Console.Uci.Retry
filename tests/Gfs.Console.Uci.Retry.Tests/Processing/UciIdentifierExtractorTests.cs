using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Processing;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Processing;
public sealed class UciIdentifierExtractorTests
{
    private static bool Try(out string? id, params string[] segments) =>
        new UciIdentifierExtractor(TestData.Options()).TryExtract(segments, out id, out _);

    [Fact]
    public void RepeatedSameIdentifier_IsExtracted()
    {
        Assert.True(Try(out var id, "abc 100-2000000-300 Contract not created.", "\r\n100-2000000-300"));
        Assert.Equal("100-2000000-300", id);
    }

    [Theory]
    [InlineData("no identifier", "")]
    [InlineData("100-2000000-300", "100-2000000-301")]
    [InlineData("1100-2000000-300", "")]               // longer digit run is not a match
    public void MissingOrConflicting_Fails(string a, string b) => Assert.False(Try(out _, a, b));

    [Fact]
    public void NonFilenameSafeIdentifier_Fails()
    {
        var options = TestData.Options();
        options.ErrorFileUciIdentifierPattern = @"(?<id>\S+)";
        Assert.False(new UciIdentifierExtractor(options).TryExtract(["..\\evil"], out _, out _));
    }

    [Fact]
    public void PatternWithoutIdGroup_IsConfigurationError()
    {
        var options = TestData.Options();
        options.ErrorFileUciIdentifierPattern = @"\d+";
        Assert.Throws<InvalidOperationException>(() => new UciIdentifierExtractor(options));
    }
}
