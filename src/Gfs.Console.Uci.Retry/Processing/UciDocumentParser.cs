using Gfs.Console.Uci.Retry.Models;

namespace Gfs.Console.Uci.Retry.Processing;

/// <summary>
/// Builds a logical section view over the embedded UCI without altering it, using the official per-section data-field
/// counts (<see cref="UciLayout.DataFieldCount"/>) for deterministic parsing:
///     CU -> RP* -> CO -> (AS -> AUC -> AMB -> AMI)* -> MB* -> MI* -> RN
/// </summary>
/// <remarks>
/// Why counts instead of scanning for header text: a value such as "AS" or "CU" can legitimately appear as field
/// data, so a section-looking token is only a header at the position where the previous section's fields end. The
/// parser never guesses; a count mismatch or unexpected successor is a failure, which callers treat as manual review.
/// Empty fields are real fields and are counted. Each AS/AUC/AMB/AMI run is an ordered asset group, and group order
/// is preserved. The parser is read-only over the source text; offsets in the result point back into it.
/// <para>
/// A header is read, then exactly its data-field count of fields is consumed (empty and trailing-empty fields count,
/// and section-looking values inside that range are plain data). The next token must then be a header the grammar allows.
/// The text must end exactly when the RN section's fields end, optionally followed by one final '|' delimiter. Any deviation fails closed.
/// </para>
/// </remarks>
public static class UciDocumentParser
{
    private static readonly Dictionary<string, UciSectionType> Headers =
        Enum.GetValues<UciSectionType>().ToDictionary(t => t.ToString(), t => t, StringComparer.Ordinal);

    /// <summary>Validates the section grammar and field counts; false (with a non-sensitive <paramref name="failure"/>) on any deviation.</summary>
    public static bool TryParse(string source, out UciDocument? document, out string? failure)
    {
        document = null;
        var fields = Tokenize(source);
        var sections = new List<UciSection>();
        var index = 0;
        UciSectionType? previous = null;

        while (true)
        {
            if (index >= fields.Count)
            {
                failure = previous is null ? "The UCI is empty." : $"Expected a section after {previous}, but the UCI ended.";
                return false;
            }

            if (!Headers.TryGetValue(fields[index].Value.Trim('\r', '\n'), out var type))
            {
                failure = previous is null
                    ? "The UCI does not begin with a CU section."
                    : $"Expected {Describe(previous.Value)} after the {previous} section, but found something else.";
                return false;
            }

            if (!IsAllowed(previous, type))
            {
                failure = previous is null
                    ? "The UCI does not begin with a CU section."
                    : $"A {type} section is not permitted after the {previous} section (expected {Describe(previous.Value)}).";
                return false;
            }

            var count = UciLayout.DataFieldCount(type);
            if (index + count >= fields.Count)
            {
                failure = type == UciSectionType.AS
                    ? "An AS section does not contain enough fields to include EQUIP.ZIP."
                    : $"The {type} section has fewer than the required {count} fields.";
                return false;
            }

            sections.Add(new UciSection(type, fields[index], fields.GetRange(index + 1, count)));
            index += count + 1;
            previous = type;

            if (type == UciSectionType.RN)
            {
                // The RN fields may be followed by one final delimiter; anything else after them is unexpected.
                var hasFinalDelimiter = index == fields.Count - 1 && fields[index].Value.Length == 0;
                if (index != fields.Count && !hasFinalDelimiter)
                {
                    failure = "Content was found after the RN section.";
                    return false;
                }
                break;
            }
        }

        var groups = BuildAssetGroups(sections, out failure);
        if (groups is null)
        {
            return false;
        }

        document = new UciDocument(source, sections, groups);
        return true;
    }

    private static List<UciField> Tokenize(string source)
    {
        var fields = new List<UciField>();
        var start = 0;
        while (true)
        {
            var pipe = source.IndexOf('|', start);
            if (pipe < 0)
            {
                fields.Add(new UciField(source[start..], start));
                return fields;
            }
            fields.Add(new UciField(source[start..pipe], start));
            start = pipe + 1;
        }
    }

    private static UciSectionType[] Allowed(UciSectionType? after) => after switch
    {
        null => [UciSectionType.CU],
        UciSectionType.CU or UciSectionType.RP => [UciSectionType.RP, UciSectionType.CO],
        UciSectionType.CO or UciSectionType.AMI => [UciSectionType.AS, UciSectionType.MB, UciSectionType.MI, UciSectionType.RN],
        UciSectionType.AS => [UciSectionType.AUC],
        UciSectionType.AUC => [UciSectionType.AMB],
        UciSectionType.AMB => [UciSectionType.AMI],
        UciSectionType.MB => [UciSectionType.MB, UciSectionType.MI, UciSectionType.RN],
        UciSectionType.MI => [UciSectionType.MI, UciSectionType.RN],
        _ => []
    };

    private static bool IsAllowed(UciSectionType? after, UciSectionType type) => Allowed(after).Contains(type);

    private static string Describe(UciSectionType after) => string.Join(" or ", Allowed(after));

    private static List<UciAssetGroup>? BuildAssetGroups(List<UciSection> sections, out string? failure)
    {
        failure = null;
        var groups = new List<UciAssetGroup>();
        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].Type != UciSectionType.AS)
            {
                continue;
            }
            if (i + 3 >= sections.Count
                || sections[i + 1].Type != UciSectionType.AUC
                || sections[i + 2].Type != UciSectionType.AMB
                || sections[i + 3].Type != UciSectionType.AMI)
            {
                failure = "Incomplete asset group.";
                return null;
            }
            groups.Add(new UciAssetGroup(sections[i], sections[i + 1], sections[i + 2], sections[i + 3]));
            i += 3;
        }
        return groups;
    }
}
