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
public sealed class ErrorClassifierTests
{
    private static UciErrorType Classify(string text) => new ErrorClassifier(TestData.Options()).Classify(text);

    [Fact]
    public void ExactGeoCodeError_IsGeoCode() => Assert.Equal(UciErrorType.GeoCode, Classify("x\r\n " + TestData.GeoMessage + " Contract not created."));

    [Theory]
    [InlineData("11*222*3333 is an inactive GeoCode associated with Zip Code 99999-0000. It cannot be entered on a new asset location.")]
    [InlineData("11*222*3333 is an active GeoCode associated with Zip Code 99999. It cannot be entered on a new asset location for Asset ID 1.")]
    [InlineData("11*222*3333 is an inactive GeoCode associated with Zip Code 9999. It cannot be entered on a new asset location for Asset ID 1.")]
    [InlineData("GeoCode problem with Zip Code 99999")]
    [InlineData("Invalid zip code geo")]
    [InlineData("Something unrelated failed")]
    [InlineData("")]
    public void NonMatching_IsUnknown(string text) => Assert.Equal(UciErrorType.Unknown, Classify(text));

    private const string Fd = "\u00FD";
    private static string Msg(int asset) => TestData.GeoMessage.Replace("4444", asset.ToString());
    private static string Wrapper(params string[] messages) =>
        "\r\nex1.txt\r\n " + string.Join("", messages.Select(m => m + Fd + " 0" + Fd + " ")) + TestData.Identifier + " Contract not created.\r\n";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OneOrMoreExactMessages_AreGeoCode_WithDistinctAssetIds(int count) =>
        Assert.Equal(UciErrorType.GeoCode, Classify(Wrapper(Enumerable.Range(1, count).Select(Msg).ToArray())));

    [Fact]
    public void RepeatedIdenticalMessages_AreGeoCode() => Assert.Equal(UciErrorType.GeoCode, Classify(Wrapper(Msg(1), Msg(1))));

    [Fact]
    public void GeoCodeMessagesPlusAnotherFailure_AreUnknown()
    {
        Assert.Equal(UciErrorType.Unknown, Classify(Wrapper(Msg(1), "Invalid lessee name.", Msg(2))));
        Assert.Equal(UciErrorType.Unknown, Classify(Wrapper("Invalid lessee name.", Msg(1))));
        Assert.Equal(UciErrorType.Unknown, Classify(Wrapper(Msg(1), "Invalid lessee name.")));
    }

    [Fact]
    public void UnrecognizedStructure_AroundGeoCodeMessages_IsUnknown()
    {
        Assert.Equal(UciErrorType.Unknown, Classify("\r\nex1.txt\r\n " + Msg(1) + Fd + " 0" + Fd + " " + Msg(2) + Fd + " 0")); // no final field
        Assert.Equal(UciErrorType.Unknown, Classify("\r\nex1.txt\r\n " + Msg(1) + Fd + " x" + Fd + " " + TestData.Identifier + " Contract not created.")); // bad number field
        Assert.Equal(UciErrorType.Unknown, Classify("\r\nex1.txt\r\n " + Msg(1) + " extra" + Fd + " 0" + Fd + " " + TestData.Identifier + " Contract not created."));
        Assert.Equal(UciErrorType.Unknown, Classify("\r\nex1.txt\r\nSomething else failed\r\n " + Msg(1) + Fd + " 0" + Fd + " " + TestData.Identifier + " Contract not created."));
    }

    [Fact]
    public void RepeatedMessage_WithoutFieldDelimiters_IsUnknown() => Assert.Equal(UciErrorType.Unknown, Classify(TestData.GeoMessage + "\r\n" + TestData.GeoMessage));

    private static string Contract(string id = TestData.Identifier) => id + " Contract not created.";
    private static string Join(params string[] fields) => "\r\nex1.txt\r\n " + string.Join(Fd + " ", fields) + "\r\n";
    private static string Geo(int asset, string geo = "11*222*3333", string zip = "99999-0000") =>
        Msg(asset).Replace("11*222*3333", geo).Replace("99999-0000", zip);

    [Theory]
    [MemberData(nameof(SupportedWrappers))]
    public void FieldsInAnyOrder_AreGeoCode(string wrapper) => Assert.Equal(UciErrorType.GeoCode, Classify(wrapper));

    public static TheoryData<string> SupportedWrappers() => new()
    {
        Join("0", Contract(), Msg(1)),
        Join(Contract(), Msg(1)),
        Join(Msg(1), Contract()),
        Join(Msg(1), "0", Msg(2), Contract(), Msg(3)),
        Join(Msg(1), "0", Msg(2), "0", Msg(3), "0", "0", Contract(), Msg(4)),
        Join(Msg(10), Msg(20), Contract()),
        Join(Geo(1), Geo(2, "44*339*0650", "77385"), Geo(3, "44*201*1440", "77379-8205"), Contract()),
        Join(Msg(1), "0", "0", "0", Contract()),
    };

    [Theory]
    [MemberData(nameof(UnsupportedWrappers))]
    public void UnprovenFields_AreUnknown(string wrapper) => Assert.Equal(UciErrorType.Unknown, Classify(wrapper));

    public static TheoryData<string> UnsupportedWrappers() => new()
    {
        Join(Msg(1), "0"),
        Join(Msg(1), Contract(), Contract("100-2000000-999")),
        Join(Msg(1), Contract(), Contract()),
        Join(Msg(1), "Invalid lessee name.", Contract()),
        Join("Invalid lessee name.", "0", Contract(), Msg(1)),
        Join(Msg(1).Replace("Asset ID", "Asset"), Contract()),
        Join(Msg(1), "abc", Contract()),
        Join(Msg(1), "0 1", Contract()),
        Join("0", Contract()),
        Join(Msg(1), Contract() + " extra"),
    };

    [Fact]
    public void ContractFieldAlone_IsUnknown() =>
        Assert.Equal(UciErrorType.Unknown, Classify(Join("0", Contract())));
}