using Gfs.Console.Uci.Retry.Models;

namespace Gfs.Console.Uci.Retry.Processing;

public enum EquipZipKind
{
    EligibleNineDigit,
    EligibleHyphenatedZipPlus4,
    AlreadyFiveDigit,
    Blank,
    Unsupported
}

public enum GeoCodeRemediationStatus
{
    /// <summary>A corrected, validated UCI was produced.</summary>
    Corrected,
    /// <summary>Nothing can be corrected automatically (unsupported value or nothing to change). Expected condition.</summary>
    NotEligible,
    /// <summary>A correction was planned but could not be validated.</summary>
    Failed
}

/// <summary>One exact source-range replacement of an AS EQUIP.ZIP field.</summary>
public sealed record ZipReplacement(int AsOccurrence, UciSectionType SectionType, int FieldIndex, int Start, int OriginalLength, string Value)
{
    // Value is customer data: never print it.
    public override string ToString() => $"ZipReplacement[as={AsOccurrence + 1}, start={Start}, length={OriginalLength}]";
}

public sealed class GeoCodeRemediationResult
{
    public required GeoCodeRemediationStatus Status { get; init; }
    public int AsSectionsInspected { get; init; }
    public int EquipZipFieldsModified { get; init; }
    public string? CorrectedContent { get; init; }
    public IReadOnlyList<ZipReplacement> Replacements { get; init; } = [];
    public string? FailureReason { get; init; }

    public override string ToString() => $"GeoCodeRemediationResult[{Status}, as={AsSectionsInspected}, modified={EquipZipFieldsModified}]";
}

/// <summary>
/// Plans and applies the geo-code correction to the embedded UCI: AS EQUIP.ZIP (<see cref="UciLayout.EquipZipFieldIndex"/>)
/// of 9 digits or #####-#### becomes its first five characters. The whole document is checked before anything changes;
/// replacements are exact source ranges, never joins or string replaces, so every other byte is untouched.
/// </summary>
/// <remarks>
/// Design intent (all of it is conservative on purpose):
/// <list type="bullet">
/// <item>Every AS section is preflighted before any modification, so a single unsupported value rejects the whole
/// UCI instead of producing a partly corrected file that a human would then have to reason about.</item>
/// <item>Unsupported values (anything not blank, 5 digits, 9 digits or #####-####) are never guessed at; they fail
/// closed to manual review.</item>
/// <item>Replacements are exact source ranges rather than a global search/replace, because the same digits can appear
/// in other fields or sections where they must not change. Ranges are applied highest offset first.</item>
/// <item>The result is then proven independently: expected bytes are rebuilt from the original and compared, the
/// output is reparsed with <see cref="UciDocumentParser"/>, and every field outside the targeted EQUIP.ZIPs is
/// compared. Any mismatch is a failure, never a best-effort output.</item>
/// </list>
/// </remarks>
public static class GeoCodeRemediator
{
    private const int CorrectedLength = 5;
    private const char WrapperTerminator = '{';

    public static EquipZipKind Classify(string value)
    {
        // No trimming: whitespace is not officially blank, so it is unsupported rather than guessed.
        if (value.Length == 0)
        {
            return EquipZipKind.Blank;
        }
        if (value.Length == 5 && AllDigits(value))
        {
            return EquipZipKind.AlreadyFiveDigit;
        }
        if (value.Length == 9 && AllDigits(value))
        {
            return EquipZipKind.EligibleNineDigit;
        }
        if (value.Length == 10 && value[5] == '-' && AllDigits(value.AsSpan(0, 5)) && AllDigits(value.AsSpan(6)))
        {
            return EquipZipKind.EligibleHyphenatedZipPlus4;
        }
        return EquipZipKind.Unsupported;
    }

    /// <summary>Preflights all AS sections and, if every one is blank/5-digit/eligible, returns a validated corrected UCI; otherwise NotEligible or Failed with no content.</summary>
    public static GeoCodeRemediationResult Remediate(UciDocument document)
    {
        var inspected = document.AssetGroups.Count;
        var plan = new List<ZipReplacement>();

        for (var i = 0; i < inspected; i++)
        {
            var zip = document.AssetGroups[i].As.EquipZip;
            switch (Classify(zip.Value))
            {
                case EquipZipKind.Unsupported:
                    return Result(GeoCodeRemediationStatus.NotEligible, inspected, $"AS section {i + 1} has an unsupported EQUIP.ZIP value; no changes were made.");
                case EquipZipKind.EligibleNineDigit or EquipZipKind.EligibleHyphenatedZipPlus4:
                    plan.Add(new ZipReplacement(i, UciSectionType.AS, UciLayout.EquipZipFieldIndex, zip.Start, zip.Value.Length, zip.Value[..CorrectedLength]));
                    break;
            }
        }

        if (plan.Count == 0)
        {
            return Result(GeoCodeRemediationStatus.NotEligible, inspected, "No eligible ZIP+4 EQUIP.ZIP value was found; no changes were made.");
        }

        var corrected = Apply(document.Source, plan);
        var problem = Validate(document, plan, corrected);
        return problem is null
            ? new GeoCodeRemediationResult
            {
                Status = GeoCodeRemediationStatus.Corrected,
                AsSectionsInspected = inspected,
                EquipZipFieldsModified = plan.Count,
                CorrectedContent = corrected,
                Replacements = plan
            }
            : Result(GeoCodeRemediationStatus.Failed, inspected, problem);
    }

    /// <summary>Highest offset first, so the recorded offsets of earlier ranges stay valid.</summary>
    public static string Apply(string source, IReadOnlyList<ZipReplacement> plan)
    {
        var text = new System.Text.StringBuilder(source);
        foreach (var r in plan.OrderByDescending(r => r.Start))
        {
            text.Remove(r.Start, r.OriginalLength).Insert(r.Start, r.Value);
        }
        return text.ToString();
    }

    /// <summary>Returns null when <paramref name="corrected"/> contains exactly the planned replacements, otherwise a non-sensitive reason.</summary>
    public static string? Validate(UciDocument original, IReadOnlyList<ZipReplacement> plan, string corrected)
    {
        if (plan.Count == 0)
        {
            return "No field was changed.";
        }
        if (plan.Any(r => r.SectionType != UciSectionType.AS || r.FieldIndex != UciLayout.EquipZipFieldIndex))
        {
            return "A replacement targets something other than AS EQUIP.ZIP.";
        }

        var expected = ExpectedBytes(original.Source, plan);
        if (expected is null)
        {
            return "The replacement plan is inconsistent with the source ranges.";
        }
        if (!expected.AsSpan().SequenceEqual(ErrorFileEncoding.Encoding.GetBytes(corrected)))
        {
            return "The corrected UCI differs from the planned replacements.";
        }

        if (!UciDocumentParser.TryParse(corrected, out var reparsed, out var parseFailure))
        {
            return $"The corrected UCI failed structural validation: {parseFailure}";
        }
        if (reparsed!.Sections.Count != original.Sections.Count || reparsed.AssetGroups.Count != original.AssetGroups.Count)
        {
            return "The section or AS count changed.";
        }
        if (original.Source.EndsWith('|') != corrected.EndsWith('|'))
        {
            return "The final delimiter behavior changed.";
        }
        if (corrected.EndsWith(WrapperTerminator) || corrected.Count(c => c == WrapperTerminator) != original.Source.Count(c => c == WrapperTerminator))
        {
            return "The '{' terminator handling changed.";
        }

        var planned = plan.ToDictionary(r => r.AsOccurrence);
        for (var s = 0; s < original.Sections.Count; s++)
        {
            var before = original.Sections[s];
            var after = reparsed.Sections[s];
            if (before.Type != after.Type || before.Header.Value != after.Header.Value || before.Fields.Count != after.Fields.Count)
            {
                return "The section structure changed.";
            }
            for (var f = 0; f < before.Fields.Count; f++)
            {
                var targeted = before.Type == UciSectionType.AS && f == UciLayout.EquipZipFieldIndex;
                if (!targeted && before.Fields[f].Value != after.Fields[f].Value)
                {
                    return "A field outside AS EQUIP.ZIP changed.";
                }
            }
        }

        for (var i = 0; i < original.AssetGroups.Count; i++)
        {
            var before = original.AssetGroups[i].As.EquipZip.Value;
            var after = reparsed.AssetGroups[i].As.EquipZip.Value;
            if (planned.TryGetValue(i, out var r))
            {
                if (after != r.Value || Classify(after) != EquipZipKind.AlreadyFiveDigit)
                {
                    return "A corrected EQUIP.ZIP is not exactly five digits.";
                }
            }
            else if (after != before || Classify(before) is not (EquipZipKind.AlreadyFiveDigit or EquipZipKind.Blank))
            {
                return "An EQUIP.ZIP that should be unchanged was altered.";
            }
        }

        return null;
    }

    /// <summary>Independent construction of the expected output: copy original bytes, swapping only the planned ranges in ascending order.</summary>
    private static byte[]? ExpectedBytes(string source, IReadOnlyList<ZipReplacement> plan)
    {
        var original = ErrorFileEncoding.Encoding.GetBytes(source);
        using var output = new MemoryStream();
        var position = 0;
        foreach (var r in plan.OrderBy(r => r.Start))
        {
            if (r.Start < position || r.Start + r.OriginalLength > original.Length)
            {
                return null;
            }
            output.Write(original, position, r.Start - position);
            output.Write(ErrorFileEncoding.Encoding.GetBytes(r.Value));
            position = r.Start + r.OriginalLength;
        }
        output.Write(original, position, original.Length - position);
        return output.ToArray();
    }

    private static GeoCodeRemediationResult Result(GeoCodeRemediationStatus status, int inspected, string reason) =>
        new() { Status = status, AsSectionsInspected = inspected, FailureReason = reason };

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }
}
