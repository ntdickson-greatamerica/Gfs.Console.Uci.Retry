namespace Gfs.Console.Uci.Retry.Models;

public enum UciSectionType
{
    CU, RP, CO, AS, AUC, AMB, AMI, MB, MI, RN
}

/// <summary>Part of the UCI layout definition (not configuration).</summary>
public static class UciLayout
{
    /// <summary>
    /// EQUIP.ZIP is the 13th data field after the AS header, i.e. zero-based index 12 of <see cref="UciSection.Fields"/>
    /// (Fields excludes the header). It is the only field currently eligible for geo-code remediation; this constant
    /// is the single place that position is defined.
    /// </summary>
    public const int EquipZipFieldIndex = 12;

    /// <summary>Official number of DATA fields in each section, excluding the section header.</summary>
    public static int DataFieldCount(UciSectionType type) => type switch
    {
        UciSectionType.CU => 25,
        UciSectionType.RP => 9,
        UciSectionType.CO => 142,
        UciSectionType.AS => 79,
        UciSectionType.AUC => 28,
        UciSectionType.AMB => 19,
        UciSectionType.AMI => 14,
        UciSectionType.MB => 19,
        UciSectionType.MI => 14,
        UciSectionType.RN => 12,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

/// <summary>One pipe-delimited field. <see cref="Start"/> is its offset in the embedded UCI text.</summary>
public readonly record struct UciField(string Value, int Start)
{
    public int End => Start + Value.Length;
}

/// <summary>
/// A logical section. <see cref="Fields"/> excludes the section header; empty fields are kept.
/// <see cref="Start"/>/<see cref="End"/> bound the section in the embedded UCI text (End excludes the delimiter that follows it).
/// </summary>
public sealed class UciSection(UciSectionType type, UciField header, IReadOnlyList<UciField> fields)
{
    public UciSectionType Type { get; } = type;
    public UciField Header { get; } = header;
    public IReadOnlyList<UciField> Fields { get; } = fields;
    public int Start => Header.Start;
    public int End => Fields.Count > 0 ? Fields[^1].End : Header.End;

    /// <summary>EQUIP.ZIP of an AS section. Structural validation guarantees the field exists.</summary>
    public UciField EquipZip => Type == UciSectionType.AS
        ? Fields[UciLayout.EquipZipFieldIndex]
        : throw new InvalidOperationException("EQUIP.ZIP exists only on AS sections.");
}

public sealed record UciAssetGroup(UciSection As, UciSection Auc, UciSection Amb, UciSection Ami);

/// <summary>A logical view over the embedded UCI. <see cref="Source"/> remains the authoritative, unmodified text.</summary>
public sealed class UciDocument(string source, IReadOnlyList<UciSection> sections, IReadOnlyList<UciAssetGroup> assetGroups)
{
    public string Source { get; } = source;
    public IReadOnlyList<UciSection> Sections { get; } = sections;
    public IReadOnlyList<UciAssetGroup> AssetGroups { get; } = assetGroups;

    public override string ToString() => $"UciDocument[sections={Sections.Count}, assetGroups={AssetGroups.Count}]";
}
