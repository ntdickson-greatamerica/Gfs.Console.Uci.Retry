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
public sealed class GeoCodeRemediatorTests
{
    private static UciDocument Parse(string uci)
    {
        Assert.True(UciDocumentParser.TryParse(uci, out var doc, out var failure), failure);
        return doc!;
    }

    private static string Corrected(GeoCodeRemediationResult r)
    {
        Assert.Equal(GeoCodeRemediationStatus.Corrected, r.Status);
        return r.CorrectedContent!;
    }

    /// <summary>Same layout as the fixtures, with independent control of the EQUIP.ZIP and of the same value placed in other fields.</summary>
    private static string Build(string equipZip, string elsewhere)
    {
        var first12 = new[] { "1", "Item", "", elsewhere, elsewhere, "a5", "a6", "a7", "", "a9", "a10", "a11" };
        return UciBuilder.Join(
            UciBuilder.Sec(UciSectionType.CU, elsewhere, "c2"), UciBuilder.Rp(elsewhere), UciBuilder.Co,
            UciBuilder.As(equipZip, first12), UciBuilder.Auc, UciBuilder.Amb, UciBuilder.Ami,
            UciBuilder.Mb(elsewhere), UciBuilder.Mi(elsewhere), UciBuilder.Rn);
    }

    [Theory]
    [InlineData("", EquipZipKind.Blank)]
    [InlineData("52302", EquipZipKind.AlreadyFiveDigit)]
    [InlineData("523021234", EquipZipKind.EligibleNineDigit)]
    [InlineData("52302-1234", EquipZipKind.EligibleHyphenatedZipPlus4)]
    [InlineData(" ", EquipZipKind.Unsupported)]
    [InlineData("5230", EquipZipKind.Unsupported)]
    [InlineData("523021", EquipZipKind.Unsupported)]
    [InlineData("5230212", EquipZipKind.Unsupported)]
    [InlineData("52302123", EquipZipKind.Unsupported)]
    [InlineData("5230212345", EquipZipKind.Unsupported)]
    [InlineData("ABCDE", EquipZipKind.Unsupported)]
    [InlineData("5230A1234", EquipZipKind.Unsupported)]
    [InlineData("52302-123", EquipZipKind.Unsupported)]
    [InlineData("5230-12345", EquipZipKind.Unsupported)]
    [InlineData("52302--123", EquipZipKind.Unsupported)]
    [InlineData("52302 1234", EquipZipKind.Unsupported)]
    [InlineData(" 52302", EquipZipKind.Unsupported)]
    [InlineData("52302 ", EquipZipKind.Unsupported)]
    [InlineData(" 523021234", EquipZipKind.Unsupported)]
    [InlineData("52302-1234 ", EquipZipKind.Unsupported)]
    [InlineData("52302\n", EquipZipKind.Unsupported)]
    [InlineData("52302,52303", EquipZipKind.Unsupported)]
    [InlineData("52302/1234", EquipZipKind.Unsupported)]
    public void Classify_UsesExactAsciiRulesWithoutTrimming(string value, EquipZipKind expected) =>
        Assert.Equal(expected, GeoCodeRemediator.Classify(value));

    [Theory]
    [InlineData("523021234")]
    [InlineData("52302-1234")]
    public void EligibleValue_BecomesFirstFiveDigits(string zip)
    {
        var result = GeoCodeRemediator.Remediate(Parse(UciBuilder.Valid(zip)));

        Assert.Equal(UciBuilder.Valid("52302"), Corrected(result));
        Assert.Equal(1, result.AsSectionsInspected);
        Assert.Equal(1, result.EquipZipFieldsModified);
    }

    [Theory]
    [InlineData("52302")]
    [InlineData("")]
    public void AlreadyFiveDigitOrBlank_HasNoEligibleCorrection(string zip)
    {
        var result = GeoCodeRemediator.Remediate(Parse(UciBuilder.Valid(zip)));

        Assert.Equal(GeoCodeRemediationStatus.NotEligible, result.Status);
        Assert.Null(result.CorrectedContent);
        Assert.Empty(result.Replacements);
        Assert.Contains("No eligible ZIP+4", result.FailureReason);
    }

    [Fact]
    public void ConsecutiveAmbGroup_RoundTripsChangingOnlyEquipZip()
    {
        var tax = UciBuilder.Sec(UciSectionType.AMB, "TN Single Article Tax", "1/2027", "12/2029");
        string Build(string a, string b) => UciBuilder.Join(
            UciBuilder.Cu, UciBuilder.Co,
            UciBuilder.As(a), UciBuilder.Auc, UciBuilder.Amb, tax, UciBuilder.Ami,
            UciBuilder.Group(b), UciBuilder.Rn);

        var result = GeoCodeRemediator.Remediate(Parse(Build("373174567", "11111")));

        Assert.Equal(Build("37317", "11111"), Corrected(result));
        Assert.Equal(2, result.AsSectionsInspected);
        Assert.Equal(1, result.EquipZipFieldsModified);
    }

    [Fact]
    public void MixedAssetGroups_ModifyOnlyTheEligibleFields()
    {
        var result = GeoCodeRemediator.Remediate(Parse(UciBuilder.Valid("523021234", "12345", "52302-1234", "")));

        Assert.Equal(UciBuilder.Valid("52302", "12345", "52302", ""), Corrected(result));
        Assert.Equal(4, result.AsSectionsInspected);
        Assert.Equal(2, result.EquipZipFieldsModified);
        Assert.Equal([0, 2], result.Replacements.Select(r => r.AsOccurrence));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData(" 52302")]
    [InlineData(" ")]
    public void UnsupportedValue_RejectsWithoutAnyReplacement(string unsupported)
    {
        var result = GeoCodeRemediator.Remediate(Parse(UciBuilder.Valid(unsupported)));
        Assert.Equal(GeoCodeRemediationStatus.NotEligible, result.Status);
        Assert.Null(result.CorrectedContent);
        Assert.Empty(result.Replacements);
        if (unsupported.Trim().Length > 0)
        {
            Assert.DoesNotContain(unsupported, result.FailureReason!);
        }
    }

    [Fact]
    public void ValidCorrectionPlusUnsupportedValue_RejectsEverything()
    {
        var result = GeoCodeRemediator.Remediate(Parse(UciBuilder.Valid("523021234", "ABCDE", "52302-1234")));

        Assert.Equal(GeoCodeRemediationStatus.NotEligible, result.Status);
        Assert.Null(result.CorrectedContent);
        Assert.Equal(0, result.EquipZipFieldsModified);
        Assert.Contains("AS section 2", result.FailureReason);
    }

    [Fact]
    public void SameValueInOtherSectionsAndOtherAsFields_IsNotChanged()
    {
        var source = Build("523021234", "523021234");
        var result = GeoCodeRemediator.Remediate(Parse(source));

        Assert.Equal(Build("52302", "523021234"), Corrected(result));
        Assert.Equal(1, result.EquipZipFieldsModified);
        Assert.Equal(6, result.CorrectedContent!.Split("523021234").Length - 1); // CU, RP, AS fields 3 and 4, MB, MI
    }

    [Fact]
    public void RepeatedIdenticalEquipZips_ChangeEachExactRange()
    {
        var result = GeoCodeRemediator.Remediate(Parse(UciBuilder.Valid("523021234", "523021234", "523021234")));

        Assert.Equal(UciBuilder.Valid("52302", "52302", "52302"), Corrected(result));
        Assert.Equal(3, result.EquipZipFieldsModified);
        Assert.Equal(3, result.Replacements.Select(r => r.Start).Distinct().Count());
    }

    [Fact]
    public void ReplacementsTargetOnlyAsEquipZip_AndApplyHighestOffsetFirst()
    {
        var source = UciBuilder.Valid("523021234", "52302-1234", "523021234", "52302-1234");
        var document = Parse(source);
        var result = GeoCodeRemediator.Remediate(document);

        Assert.All(result.Replacements, r =>
        {
            Assert.Equal(UciSectionType.AS, r.SectionType);
            Assert.Equal(UciLayout.EquipZipFieldIndex, r.FieldIndex);
            Assert.Equal(document.AssetGroups[r.AsOccurrence].As.EquipZip.Start, r.Start);
        });

        // The plan is ordered lowest-first, yet applying it in any order must give identical output because Apply goes highest-first.
        var ascending = GeoCodeRemediator.Apply(source, result.Replacements);
        Assert.Equal(ascending, GeoCodeRemediator.Apply(source, result.Replacements.Reverse().ToList()));
        Assert.Equal(UciBuilder.Valid("52302", "52302", "52302", "52302"), ascending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalPipe_IsPreservedWhenPresentAndNotAddedWhenAbsent(bool finalPipe)
    {
        var rnTail = finalPipe ? "|" : "z"; // with no final delimiter, make RN's last field non-empty so the text does not end in '|'
        var source = UciBuilder.Valid("523021234") + rnTail;
        var corrected = Corrected(GeoCodeRemediator.Remediate(Parse(source)));

        Assert.Equal(finalPipe, corrected.EndsWith('|'));
        Assert.Equal(UciBuilder.Valid("52302") + rnTail, corrected);
    }

    [Fact]
    public void LineEndingsAndEmptyAndTrailingEmptyFields_ArePreserved()
    {
        static string Shape(string s) => s.Replace("|AUC|", "|\r\nAUC|").Replace("|CO|", "|\nCO|").Replace("c2||", "c2\r\n||").Replace("|Item|", "|\u00FD\u00E9 It\r\nem |");
        var source = Shape(UciBuilder.Valid("523021234", "", "52302-1234")) + "|";
        var result = GeoCodeRemediator.Remediate(Parse(source));
        var corrected = Corrected(result);

        Assert.Equal(Shape(UciBuilder.Valid("52302", "", "52302")) + "|", corrected);
        Assert.Equal(source.Count(c => c == '\r'), corrected.Count(c => c == '\r'));
        Assert.Equal(source.Count(c => c == '\n'), corrected.Count(c => c == '\n'));
        Assert.Equal(source.Count(c => c == '|'), corrected.Count(c => c == '|') + 0);
        Assert.EndsWith("||||||||||||", corrected);
    }

    [Fact]
    public void CorrectedUci_ReparsesWithSameStructure_AndBytesEqualExpected_AndOnlyTargetRangesDiffer()
    {
        var source = UciBuilder.Valid("523021234", "12345", "52302-1234", "") + "|";
        var original = Parse(source);
        var result = GeoCodeRemediator.Remediate(original);
        var corrected = Corrected(result);

        var reparsed = Parse(corrected);
        Assert.Equal(original.Sections.Count, reparsed.Sections.Count);
        Assert.Equal(original.AssetGroups.Count, reparsed.AssetGroups.Count);
        Assert.Equal(original.Sections.Select(s => s.Type), reparsed.Sections.Select(s => s.Type));
        Assert.All(reparsed.AssetGroups.Select(g => g.As.EquipZip.Value), z => Assert.Matches("^([0-9]{5}|)$", z));

        // Independently built expected bytes: ascending copy of the original bytes with only the planned ranges swapped.
        var originalBytes = ErrorFileEncoding.Encoding.GetBytes(source);
        var expected = new List<byte>();
        var position = 0;
        foreach (var r in result.Replacements.OrderBy(r => r.Start))
        {
            expected.AddRange(originalBytes[position..r.Start]);
            expected.AddRange(ErrorFileEncoding.Encoding.GetBytes(r.Value));
            position = r.Start + r.OriginalLength;
            // every byte between the previous and this range is identical in the output, and the range itself is the old value.
            Assert.Equal(source.Substring(r.Start, r.OriginalLength)[..5], r.Value);
        }
        expected.AddRange(originalBytes[position..]);
        Assert.Equal(expected.ToArray(), ErrorFileEncoding.Encoding.GetBytes(corrected));

        // No bytes outside the target ranges differ: removing the ranges from both texts leaves identical remainders.
        string Strip(string text, IEnumerable<(int Start, int Length)> ranges) =>
            ranges.OrderByDescending(r => r.Start).Aggregate(text, (t, r) => t.Remove(r.Start, r.Length));
        var oldRanges = result.Replacements.Select(r => (r.Start, r.OriginalLength)).ToList();
        var shift = 0;
        var newRanges = new List<(int, int)>();
        foreach (var r in result.Replacements.OrderBy(r => r.Start))
        {
            newRanges.Add((r.Start - shift, r.Value.Length));
            shift += r.OriginalLength - r.Value.Length;
        }
        Assert.Equal(Strip(source, oldRanges), Strip(corrected, newRanges));
    }

    [Fact]
    public void BraceTerminator_NeverAppearsInCorrectedOutput_ButBracesInDataAreKept()
    {
        var source = UciBuilder.Join(UciBuilder.Sec(UciSectionType.CU, "a{b"), UciBuilder.Co, UciBuilder.Group("523021234"), UciBuilder.Rn) + "|";
        var corrected = Corrected(GeoCodeRemediator.Remediate(Parse(source)));

        Assert.False(corrected.EndsWith('{'));
        Assert.Equal(1, corrected.Count(c => c == '{'));
        Assert.Contains("a{b", corrected);
    }

    [Fact]
    public void Original_Document_IsLeftUnchanged()
    {
        var source = UciBuilder.Valid("523021234");
        var document = Parse(source);
        GeoCodeRemediator.Remediate(document);

        Assert.Equal(source, document.Source);
        Assert.Equal("523021234", document.AssetGroups[0].As.EquipZip.Value);
    }

    [Fact]
    public void Validate_DetectsAnyDeviationFromThePlan()
    {
        var source = UciBuilder.Valid("523021234", "523021234") + "|";
        var document = Parse(source);
        var result = GeoCodeRemediator.Remediate(document);
        var good = Corrected(result);
        var plan = result.Replacements;

        Assert.Null(GeoCodeRemediator.Validate(document, plan, good));
        Assert.NotNull(GeoCodeRemediator.Validate(document, plan, good.Replace("c1", "cX")));              // unrelated byte changed
        Assert.NotNull(GeoCodeRemediator.Validate(document, plan, good[..^1]));                           // final pipe dropped
        Assert.NotNull(GeoCodeRemediator.Validate(document, plan, good + "{"));                           // terminator appended
        Assert.NotNull(GeoCodeRemediator.Validate(document, plan, source));                               // nothing changed
        Assert.NotNull(GeoCodeRemediator.Validate(document, plan.Take(1).ToList(), good));                // second change not in plan
        Assert.NotNull(GeoCodeRemediator.Validate(document, [], good));
        Assert.NotNull(GeoCodeRemediator.Validate(document, [plan[0] with { SectionType = UciSectionType.CU }], good));
        Assert.NotNull(GeoCodeRemediator.Validate(document, [plan[0] with { FieldIndex = 11 }], good));
    }

    [Fact]
    public void Validate_RejectsBadReplacementValue()
    {
        var document = Parse(UciBuilder.Valid("523021234"));
        var plan = GeoCodeRemediator.Remediate(document).Replacements;
        var bad = plan.Select(r => r with { Value = "5230" }).ToList();
        Assert.NotNull(GeoCodeRemediator.Validate(document, bad, GeoCodeRemediator.Apply(document.Source, bad)));
    }
}
