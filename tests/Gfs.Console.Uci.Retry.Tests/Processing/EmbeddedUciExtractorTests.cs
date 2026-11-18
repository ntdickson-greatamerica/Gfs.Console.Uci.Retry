using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Models;
using Gfs.Console.Uci.Retry.Processing;
using Gfs.Console.Uci.Retry.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Processing;
public sealed class EmbeddedUciExtractorTests
{
    private static EmbeddedUci Extract(string text)
    {
        Assert.True(EmbeddedUciExtractor.TryExtract(text, out var uci, out var failure), failure);
        return uci!;
    }

    [Fact]
    public void ExtractsExactUci_ExcludingTerminatorAndContentBeforeAndAfter()
    {
        var text = "header\r\nmore text\r\n" + TestData.Uci + "{\r\ntrailer 100-2000000-300";
        var uci = Extract(text);
        Assert.Equal(TestData.Uci, uci.Text);
        Assert.False(uci.Text.EndsWith('{'));
        Assert.Equal(text.IndexOf(TestData.Uci, StringComparison.Ordinal), uci.StartOffset);
        Assert.Equal(uci.StartOffset + TestData.Uci.Length, uci.EndOffset);
        Assert.Equal('{', text[uci.EndOffset]);
    }

    [Fact]
    public void UciAtStartAndEndOfFile_IsExtracted() => Assert.Equal(TestData.Uci, Extract(TestData.Uci + "{").Text);

    [Fact]
    public void OnlyFinalBraceIsTerminator_OtherBracesArePreserved()
    {
        Assert.Equal("CU|a{b|RN||{", Extract("x\r\nCU|a{b|RN||{{\r\n").Text);
    }

    [Fact]
    public void PreservesLineBreaksWithinPayload_AndEmptyFields()
    {
        var uci = "CU|a||b\r\n|c|||\nRP||\r\nCO|\r\n|d|RN||x|";
        var text = "pre\r\n" + uci + "{\r\npost";
        Assert.Equal(uci, Extract(text).Text);
    }

    [Fact]
    public void PreservesTrailingWhitespaceAndNonAsciiBytes()
    {
        var uci = "CU|a \t|\u00FD\u00E9 |RN|| ";
        Assert.Equal(uci, Extract("x\n" + uci + "{\nz").Text);
    }

    [Fact]
    public void Utf8Bom_IsNotPartOfUci()
    {
        var text = "\u00EF\u00BB\u00BF" + TestData.Uci + "{";
        Assert.Equal(TestData.Uci, Extract(text).Text);
    }

    [Fact]
    public void RoundTripsBytesExactly()
    {
        var bytes = new byte[] { 0x0D, 0x0A, 0xFD, 0x30, 0xFD, 0x0D, 0x0A }.Concat(Encoding.ASCII.GetBytes("CU|a||b|RN||{")).Concat(new byte[] { 0x0D, 0x0A, 0x80 }).ToArray();
        var text = ErrorFileEncoding.Encoding.GetString(bytes);
        var uci = Extract(text);
        Assert.Equal("CU|a||b|RN||", uci.Text);
        Assert.Equal(bytes, ErrorFileEncoding.Encoding.GetBytes(text[..uci.StartOffset] + uci.Text + text[uci.EndOffset..]));
    }

    [Theory]
    [InlineData("no uci here")]
    [InlineData("junk CU|1|RN||{ inline start")]              // CU not at a line start
    [InlineData("CU|1|RN||{\r\nCU|2|RN||{")]                   // two UCIs
    [InlineData("\r\nCU|1\r\nCU|2|RN||{")]                     // two possible starts
    [InlineData("CU|1|RN||x{\r\nlater |RN|y{")]                // two possible ends
    [InlineData("CU|1|a{\r\nb|RN||{")]                         // brace at a line end inside the payload and a real terminator
    [InlineData("RN||{\r\nCU|1|CO|")]                          // terminator only before CU
    [InlineData("CU|1|RN||")]                                  // no terminator
    [InlineData("CU|1|RN||{x")]                                // content after terminator on same line
    [InlineData("CU|1|RN|{ ")]                                 // whitespace after terminator
    public void AmbiguousOrMalformed_Fails(string text)
    {
        Assert.False(EmbeddedUciExtractor.TryExtract(text, out var uci, out var failure));
        Assert.Null(uci);
        Assert.NotNull(failure);
    }
}
