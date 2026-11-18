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
public sealed class UciDocumentParserTests
{
    private static UciDocument Parse(string uci)
    {
        Assert.True(UciDocumentParser.TryParse(uci, out var doc, out var failure), failure);
        return doc!;
    }

    private static string Failure(string uci)
    {
        Assert.False(UciDocumentParser.TryParse(uci, out var doc, out var failure));
        Assert.Null(doc);
        Assert.False(string.IsNullOrWhiteSpace(failure));
        return failure!;
    }

    private static string Rebuild(UciDocument doc) =>
        string.Join("|", doc.Sections.SelectMany(s => s.Fields.Prepend(s.Header)).Select(f => f.Value));

    private static string[] Types(UciDocument doc) => doc.Sections.Select(s => s.Type.ToString()).ToArray();

    private static string Join(params string[] sections) => UciBuilder.Join(sections);

    private static string WithGroup(params string[] sections) => Join([UciBuilder.Cu, UciBuilder.Co, .. sections, UciBuilder.Rn]);

    [Fact]
    public void ValidDocument_IsUnchangedAndReassemblesByteIdentical()
    {
        var source = TestData.Uci;
        var doc = Parse(source);

        Assert.Equal(source, doc.Source);
        Assert.Equal(source, Rebuild(doc));
        Assert.All(doc.Sections, s => Assert.Equal(s.Header.Value, source[s.Start..s.Header.End]));
        Assert.All(doc.Sections.SelectMany(s => s.Fields), f => Assert.Equal(f.Value, source[f.Start..f.End]));
    }

    [Fact]
    public void EverySectionHasExactlyItsOfficialFieldCount()
    {
        var doc = Parse(TestData.Uci);
        Assert.All(doc.Sections, s => Assert.Equal(UciLayout.DataFieldCount(s.Type), s.Fields.Count));
        Assert.Equal(
            new Dictionary<UciSectionType, int> { [UciSectionType.CU] = 25, [UciSectionType.RP] = 9, [UciSectionType.CO] = 142, [UciSectionType.AS] = 79, [UciSectionType.AUC] = 28, [UciSectionType.AMB] = 19, [UciSectionType.AMI] = 14, [UciSectionType.MB] = 19, [UciSectionType.MI] = 14, [UciSectionType.RN] = 12 },
            Enum.GetValues<UciSectionType>().ToDictionary(t => t, UciLayout.DataFieldCount));
    }

    [Fact]
    public void PopulatedFields_AreParsedExactly()
    {
        string Full(UciSectionType t) => UciBuilder.Sec(t, Enumerable.Range(0, UciLayout.DataFieldCount(t)).Select(i => $"{t}{i}").ToArray());
        var source = Join(Full(UciSectionType.CU), Full(UciSectionType.CO), Full(UciSectionType.AS), Full(UciSectionType.AUC), Full(UciSectionType.AMB), Full(UciSectionType.AMI), Full(UciSectionType.RN));
        var doc = Parse(source);

        Assert.Equal("AS12", doc.AssetGroups[0].As.EquipZip.Value);
        Assert.Equal("CU24", doc.Sections[0].Fields[^1].Value);
        Assert.Equal("RN11", doc.Sections[^1].Fields[^1].Value);
        Assert.Equal(source, Rebuild(doc));
    }

    [Fact]
    public void EmptyAndTrailingEmptyFields_AreCountedAndPreserved()
    {
        var source = Join(UciBuilder.Sec(UciSectionType.CU), UciBuilder.Sec(UciSectionType.CO), UciBuilder.Sec(UciSectionType.AS), UciBuilder.Sec(UciSectionType.AUC), UciBuilder.Sec(UciSectionType.AMB), UciBuilder.Sec(UciSectionType.AMI), UciBuilder.Sec(UciSectionType.RN));
        var doc = Parse(source);

        Assert.All(doc.Sections, s => Assert.All(s.Fields, f => Assert.Equal("", f.Value)));
        Assert.All(doc.Sections, s => Assert.Equal(UciLayout.DataFieldCount(s.Type), s.Fields.Count));
        Assert.EndsWith("|", source);
        Assert.Equal(source, Rebuild(doc));
        Assert.Equal("", doc.AssetGroups[0].As.EquipZip.Value);
    }

    [Theory]
    [InlineData(UciSectionType.CU)]
    [InlineData(UciSectionType.CO)]
    [InlineData(UciSectionType.AS)]
    [InlineData(UciSectionType.AUC)]
    [InlineData(UciSectionType.AMB)]
    [InlineData(UciSectionType.AMI)]
    [InlineData(UciSectionType.RN)]
    public void EverySectionAbbreviationAsDataInsideAFieldRange_IsOrdinaryData(UciSectionType holder)
    {
        string S(UciSectionType t) => t == holder ? UciBuilder.AllAbbreviations(t) : UciBuilder.Sec(t);
        var source = Join(S(UciSectionType.CU), S(UciSectionType.CO), S(UciSectionType.AS), S(UciSectionType.AUC), S(UciSectionType.AMB), S(UciSectionType.AMI), S(UciSectionType.RN));
        var doc = Parse(source);

        Assert.Equal(["CU", "CO", "AS", "AUC", "AMB", "AMI", "RN"], Types(doc));
        Assert.Equal(source, Rebuild(doc));
        var held = doc.Sections.Single(s => s.Type == holder);
        Assert.Equal(Enum.GetNames<UciSectionType>(), held.Fields.Take(10).Select(f => f.Value));
    }

    [Fact]
    public void RepeatableSectionsWithAbbreviationData_AreOrdinaryData()
    {
        var source = Join(UciBuilder.Cu, UciBuilder.AllAbbreviations(UciSectionType.RP), UciBuilder.Co, UciBuilder.Group("1"),
            UciBuilder.AllAbbreviations(UciSectionType.MB), UciBuilder.AllAbbreviations(UciSectionType.MI), UciBuilder.Rn);
        Assert.Equal(["CU", "RP", "CO", "AS", "AUC", "AMB", "AMI", "MB", "MI", "RN"], Types(Parse(source)));
    }

    [Fact]
    public void AsInAddressStateField_IsDataAndEquipZipStillCorrect()
    {
        var first12 = new[] { "1", "Item", "", "AS", "AS", "AS", "AS", "AS", "", "AS", "AS", "AS" };
        var doc = Parse(WithGroup(UciBuilder.As("99999", first12), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami));

        Assert.Equal(["CU", "CO", "AS", "AUC", "AMB", "AMI", "RN"], Types(doc));
        Assert.Equal("AS", doc.AssetGroups[0].As.Fields[3].Value);
        Assert.Equal("99999", doc.AssetGroups[0].As.EquipZip.Value);
    }

    [Fact]
    public void CuLikeValueInsideCu_DoesNotStartANewDocument()
    {
        var doc = Parse(Join(UciBuilder.Sec(UciSectionType.CU, "CU", "CO", "RN"), UciBuilder.Co, UciBuilder.Group("1"), UciBuilder.Rn));
        Assert.Equal(["CU", "CO", "AS", "AUC", "AMB", "AMI", "RN"], Types(doc));
    }

    [Fact]
    public void MultipleRp_ArePreservedInOrder()
    {
        var doc = Parse(Join(UciBuilder.Cu, UciBuilder.Rp("first"), UciBuilder.Rp("", "second"), UciBuilder.Rp("third"), UciBuilder.Co, UciBuilder.Group("1"), UciBuilder.Rn));
        Assert.Equal(["CU", "RP", "RP", "RP", "CO", "AS", "AUC", "AMB", "AMI", "RN"], Types(doc));
        var firsts = doc.Sections.Where(s => s.Type == UciSectionType.RP).Select(s => s.Fields[0].Value + "/" + s.Fields[1].Value).ToArray();
        Assert.Equal(["first/", "/second", "third/"], firsts);
    }

    [Fact]
    public void SingleCompleteAssetGroup_Parses()
    {
        var doc = Parse(UciBuilder.Valid("99999"));
        var group = Assert.Single(doc.AssetGroups);
        Assert.Equal(
            [UciSectionType.AS, UciSectionType.AUC, UciSectionType.AMB, UciSectionType.AMI],
            new[] { group.As.Type, group.Auc.Type, group.Amb.Type, group.Ami.Type });
    }

    [Fact]
    public void MultipleAssetGroups_PreserveOrderAndOwnZip()
    {
        var doc = Parse(UciBuilder.Valid("11111", "22222-3333", "", "523021234"));
        Assert.Equal(["11111", "22222-3333", "", "523021234"], doc.AssetGroups.Select(g => g.As.EquipZip.Value));
        Assert.True(doc.AssetGroups.Zip(doc.AssetGroups.Skip(1)).All(p => p.First.Ami.End < p.Second.As.Start));
        Assert.Equal(["CU", "CO", "AS", "AUC", "AMB", "AMI", "AS", "AUC", "AMB", "AMI", "AS", "AUC", "AMB", "AMI", "AS", "AUC", "AMB", "AMI", "RN"], Types(doc));
    }

    private static string TaxAmb => UciBuilder.Sec(UciSectionType.AMB, "TN Single Article Tax", "1/2027", "12/2029");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ConsecutiveAmbSections_ParseAsOneAssetGroup(int ambCount)
    {
        var ambs = Enumerable.Range(0, ambCount).Select(i => i == 0 ? UciBuilder.Amb : TaxAmb).ToArray();
        var source = WithGroup([UciBuilder.As("11111"), UciBuilder.Auc, .. ambs, UciBuilder.Ami]);
        var doc = Parse(source);

        var group = Assert.Single(doc.AssetGroups);
        Assert.Equal("11111", group.As.EquipZip.Value);
        Assert.Equal(UciSectionType.AMI, group.Ami.Type);
        Assert.Equal(ambCount, doc.Sections.Count(s => s.Type == UciSectionType.AMB));
        Assert.Equal(source, Rebuild(doc));
    }

    [Fact]
    public void MixedAmbCounts_EachGroupKeepsItsOwnAsAndAmi()
    {
        var source = WithGroup(
            UciBuilder.Group("11111"),
            UciBuilder.As("22222-3333"), UciBuilder.Auc, UciBuilder.Amb, TaxAmb, UciBuilder.Ami,
            UciBuilder.Group("33333"));
        var doc = Parse(source);

        Assert.Equal(["11111", "22222-3333", "33333"], doc.AssetGroups.Select(g => g.As.EquipZip.Value));
        Assert.Equal(["CU", "CO", "AS", "AUC", "AMB", "AMI", "AS", "AUC", "AMB", "AMB", "AMI", "AS", "AUC", "AMB", "AMI", "RN"], Types(doc));
        Assert.All(doc.AssetGroups, g => Assert.Equal(UciSectionType.AMI, g.Ami.Type));
        Assert.True(doc.AssetGroups.Zip(doc.AssetGroups.Skip(1)).All(p => p.First.Ami.End < p.Second.As.Start));
        Assert.True(doc.AssetGroups[1].As.End < doc.AssetGroups[1].Ami.Start);
        Assert.Equal(source, Rebuild(doc));
    }

    [Theory]
    [InlineData("ambAmbNoAmi")]
    [InlineData("ambAmbThenAs")]
    public void ConsecutiveAmbWithoutTerminatingAmi_Fails(string scenario)
    {
        var uci = scenario == "ambAmbNoAmi"
            ? WithGroup(UciBuilder.As("1"), UciBuilder.Auc, UciBuilder.Amb, TaxAmb)
            : WithGroup(UciBuilder.As("1"), UciBuilder.Auc, UciBuilder.Amb, TaxAmb, UciBuilder.As("2"));
        Failure(uci);
    }

    [Fact]
    public void MultipleMbAndMi_ParseInOrder()
    {
        var doc = Parse(WithGroup(UciBuilder.Group("1"), UciBuilder.Mb("b1"), UciBuilder.Mb("b2"), UciBuilder.Mb("", "b3"), UciBuilder.Mi("i1"), UciBuilder.Mi("i2")));
        Assert.Equal(["CU", "CO", "AS", "AUC", "AMB", "AMI", "MB", "MB", "MB", "MI", "MI", "RN"], Types(doc));
    }

    [Fact]
    public void ZeroAssetGroups_ParseWithNoGroups()
    {
        var doc = Parse(Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.Rn));
        Assert.Empty(doc.AssetGroups);
    }

    [Fact]
    public void EquipZip_IsDataFieldIndexTwelve_AndHasSourcePosition()
    {
        var source = UciBuilder.Valid("55555");
        var doc = Parse(source);
        var zip = doc.AssetGroups[0].As.EquipZip;

        Assert.Equal(12, UciLayout.EquipZipFieldIndex);
        Assert.Equal("55555", zip.Value);
        Assert.Equal(doc.AssetGroups[0].As.Fields[UciLayout.EquipZipFieldIndex], zip);
        Assert.Equal("55555", source.Substring(zip.Start, zip.Value.Length));
        Assert.Throws<InvalidOperationException>(() => _ = doc.Sections[0].EquipZip);
    }

    [Fact]
    public void PhysicalLineBreaks_ArePreserved_IncludingAroundHeadersAndInsideFields()
    {
        var source = UciBuilder.Valid("99999").Replace("c2||c4", "c2\r\nmore||c4").Replace("|CO|", "|\r\nCO|").Replace("|RN|", "|\nRN|");
        var doc = Parse(source);

        Assert.Equal(source, Rebuild(doc));
        Assert.Equal("c2\r\nmore", doc.Sections[0].Fields[1].Value);
        Assert.Equal("\r\nCO", doc.Sections[1].Header.Value);
        Assert.Equal(["CU", "CO", "AS", "AUC", "AMB", "AMI", "RN"], Types(doc));
    }

    [Fact]
    public void ExactRnCompletion_WithNothingAfterIt()
    {
        var doc = Parse(UciBuilder.Valid("1"));
        Assert.Equal(12, doc.Sections[^1].Fields.Count);
        Assert.Equal(doc.Source.Length, doc.Sections[^1].Fields[^1].End);
    }

    [Theory]
    [InlineData(UciSectionType.CU)]
    [InlineData(UciSectionType.RP)]
    [InlineData(UciSectionType.CO)]
    [InlineData(UciSectionType.AS)]
    [InlineData(UciSectionType.AUC)]
    [InlineData(UciSectionType.AMB)]
    [InlineData(UciSectionType.AMI)]
    [InlineData(UciSectionType.MB)]
    [InlineData(UciSectionType.MI)]
    [InlineData(UciSectionType.RN)]
    public void TooFewOrTooManyFields_InAnySection_Fails(UciSectionType broken)
    {
        string S(UciSectionType t, int delta = 0) =>
            t == broken ? t + "|" + string.Join("|", Enumerable.Repeat("v", UciLayout.DataFieldCount(t) + delta)) : UciBuilder.Sec(t);

        string Doc(int delta) => Join(S(UciSectionType.CU, delta), S(UciSectionType.RP, delta), S(UciSectionType.CO, delta), S(UciSectionType.AS, delta),
            S(UciSectionType.AUC, delta), S(UciSectionType.AMB, delta), S(UciSectionType.AMI, delta), S(UciSectionType.MB, delta), S(UciSectionType.MI, delta), S(UciSectionType.RN, delta));

        Parse(Doc(0));
        Failure(Doc(-1));
        Failure(Doc(1));
    }

    [Fact]
    public void AsWithTooFewFields_ReportsEquipZipProblem()
    {
        var shortAs = "AS|" + string.Join("|", Enumerable.Repeat("x", 12));
        Assert.Contains("EQUIP.ZIP", Failure(Join(UciBuilder.Cu, UciBuilder.Co, shortAs)));
    }

    [Fact]
    public void AsWithThirteenFieldsButNotTheRest_Fails()
    {
        var thirteen = "AS|" + string.Join("|", Enumerable.Repeat("x", 13));
        Failure(Join(UciBuilder.Cu, UciBuilder.Co, thirteen, UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami, UciBuilder.Rn));
    }

    [Theory]
    [InlineData("cuThenAs")]
    [InlineData("cuThenRn")]
    [InlineData("rpAfterCo")]
    [InlineData("coTwice")]
    [InlineData("duplicateCu")]
    [InlineData("asThenAmb")]
    [InlineData("asThenMb")]
    [InlineData("aucThenAmi")]
    [InlineData("ambThenAs")]
    [InlineData("rnAfterAs")]
    [InlineData("asAfterMb")]
    [InlineData("asAfterMi")]
    [InlineData("mbAfterMi")]
    [InlineData("rpAfterMb")]
    [InlineData("missingCu")]
    [InlineData("unknownSection")]
    public void WrongSuccessorAfterExactFieldCount_Fails(string scenario)
    {
        var uci = scenario switch
        {
            "cuThenAs" => Join(UciBuilder.Cu, UciBuilder.Group("1"), UciBuilder.Rn),
            "cuThenRn" => Join(UciBuilder.Cu, UciBuilder.Rn),
            "rpAfterCo" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.Rp(), UciBuilder.Rn),
            "coTwice" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.Co, UciBuilder.Rn),
            "duplicateCu" => Join(UciBuilder.Cu, UciBuilder.Cu, UciBuilder.Co, UciBuilder.Rn),
            "asThenAmb" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("1"), UciBuilder.Amb, UciBuilder.Auc, UciBuilder.Ami, UciBuilder.Rn),
            "asThenMb" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("1"), UciBuilder.Mb(), UciBuilder.Rn),
            "aucThenAmi" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("1"), UciBuilder.Auc, UciBuilder.Ami, UciBuilder.Rn),
            "ambThenAs" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("1"), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.As("2"), UciBuilder.Rn),
            "rnAfterAs" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("1"), UciBuilder.Rn),
            "asAfterMb" => WithGroup(UciBuilder.Mb(), UciBuilder.Group("1")),
            "asAfterMi" => WithGroup(UciBuilder.Mi(), UciBuilder.Group("1")),
            "mbAfterMi" => WithGroup(UciBuilder.Mi(), UciBuilder.Mb()),
            "rpAfterMb" => WithGroup(UciBuilder.Mb(), UciBuilder.Rp()),
            "missingCu" => Join(UciBuilder.Co, UciBuilder.Group("1"), UciBuilder.Rn),
            _ => Join(UciBuilder.Cu, UciBuilder.Co, "XX|" + string.Join("|", new string[10]), UciBuilder.Rn),
        };
        Failure(uci);
    }

    [Theory]
    [InlineData("missingRn")]
    [InlineData("endsAfterCu")]
    [InlineData("endsAfterAs")]
    [InlineData("empty")]
    public void MissingSuccessor_Fails(string scenario)
    {
        var uci = scenario switch
        {
            "missingRn" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.Group("1")),
            "endsAfterCu" => UciBuilder.Cu,
            "endsAfterAs" => Join(UciBuilder.Cu, UciBuilder.Co, UciBuilder.As("1")),
            _ => "",
        };
        Failure(uci);
    }

    [Theory]
    [InlineData("noAuc")]
    [InlineData("noAmb")]
    [InlineData("noAmi")]
    [InlineData("noAuc-AmbAmiOnly")]
    public void IncompleteAssetGroup_Fails(string scenario)
    {
        var uci = scenario switch
        {
            "noAuc" => WithGroup(UciBuilder.As("1")),
            "noAmb" => WithGroup(UciBuilder.As("1"), UciBuilder.Auc),
            "noAmi" => WithGroup(UciBuilder.As("1"), UciBuilder.Auc, UciBuilder.Amb),
            _ => WithGroup(UciBuilder.Amb, UciBuilder.Ami),
        };
        Failure(uci);
    }

    [Fact]
    public void SecondGroupIncomplete_FailsWholeDocument() =>
        Failure(WithGroup(UciBuilder.Group("1"), UciBuilder.As("2"), UciBuilder.Auc, UciBuilder.Amb));

    [Theory]
    [InlineData("|extra")]
    [InlineData("||")]
    [InlineData("|\r\n")]
    public void ContentAfterRn_Fails(string extra) => Failure(UciBuilder.Valid("1") + extra);

    [Fact]
    public void SingleFinalDelimiterAfterRn_IsAcceptedAndRnStillHasTwelveFields()
    {
        var source = UciBuilder.Valid("1") + "|";
        var doc = Parse(source);

        Assert.Equal(source, doc.Source);
        Assert.Equal(12, doc.Sections[^1].Fields.Count);
        Assert.Equal(source.Length - 1, doc.Sections[^1].Fields[^1].End);
    }

    [Fact]
    public void SectionAfterRn_Fails() => Failure(UciBuilder.Valid("1") + "|" + UciBuilder.Group("2"));

    [Fact]
    public void BraceTerminator_IsExcludedFromEmbeddedUci_AndNeverInReconstructedUci()
    {
        var text = "\r\nwrapper\r\n" + UciBuilder.Valid("12345") + "|{\r\n100-2000000-300";
        Assert.True(EmbeddedUciExtractor.TryExtract(text, out var uci, out _));
        Assert.True(UciDocumentParser.TryParse(uci!.Text, out var doc, out var failure), failure);

        Assert.DoesNotContain('{', uci.Text);
        Assert.EndsWith("|", uci.Text);
        Assert.Equal(UciBuilder.Valid("12345") + "|", doc!.Source);
        Assert.DoesNotContain('{', Rebuild(doc));
        Assert.Equal(uci.Text, Rebuild(doc) + "|");
    }
}
