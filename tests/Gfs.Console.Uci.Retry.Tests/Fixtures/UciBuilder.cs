using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gfs.Console.Uci.Retry.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gfs.Console.Uci.Retry.Tests.Fixtures;
/// <summary>Synthetic UCI sections with the official exact data-field counts; unspecified trailing fields are empty.</summary>
internal static class UciBuilder
{
    public static string Sec(UciSectionType type, params string[] data)
    {
        var count = UciLayout.DataFieldCount(type);
        Assert.True(data.Length <= count);
        return type + "|" + string.Join("|", data.Concat(Enumerable.Repeat("", count - data.Length)));
    }

    public static string Cu => Sec(UciSectionType.CU, "c1", "c2", "", "c4");
    public static string Co => Sec(UciSectionType.CO, "E", "", "x", "y");
    public static string Auc => Sec(UciSectionType.AUC, "u1", "", "u3");
    public static string Amb => Sec(UciSectionType.AMB, "", "b2");
    public static string Ami => Sec(UciSectionType.AMI, "i1");
    public static string Rn => Sec(UciSectionType.RN);
    public static string Rp(params string[] data) => Sec(UciSectionType.RP, data);
    public static string Mb(params string[] data) => Sec(UciSectionType.MB, data);
    public static string Mi(params string[] data) => Sec(UciSectionType.MI, data);

    public static string Join(params string[] sections) => string.Join("|", sections);

    /// <param name="first12">Optional replacement for data fields 0-11 (must be 12 values).</param>
    public static string As(string zip, string[]? first12 = null)
    {
        first12 ??= ["1", "Item", "", "a3", "a4", "a5", "a6", "a7", "", "a9", "a10", "a11"];
        Assert.Equal(12, first12.Length);
        return Sec(UciSectionType.AS, [.. first12, zip, "tail"]);
    }

    public static string Group(string zip) => Join(As(zip), Auc, Amb, Ami);

    public static string Valid(params string[] zips) => Join([Cu, Co, .. zips.Select(Group), Rn]);

    /// <summary>Every data field holds a section abbreviation, cycling through all of them.</summary>
    public static string AllAbbreviations(UciSectionType type)
    {
        var names = Enum.GetNames<UciSectionType>();
        return Sec(type, Enumerable.Range(0, UciLayout.DataFieldCount(type)).Select(i => names[i % names.Length]).ToArray());
    }
}
