using System.Text.RegularExpressions;
using Gfs.Console.Uci.Retry.Configuration;

namespace Gfs.Console.Uci.Retry.Processing;

/// <summary>
/// Extracts the retry-tracking identifier using <see cref="ProcessorOptions.ErrorFileUciIdentifierPattern"/>
/// (must define a named group "id"). The identifier is diagnostic/correlation metadata only (logs, result, marker
/// content); it never names a file and never locates one.
/// Exactly one distinct, filename-safe value must exist outside the UCI.
/// </summary>
public sealed partial class UciIdentifierExtractor
{
    private const string GroupName = "id";
    private readonly Regex _pattern;

    public UciIdentifierExtractor(ProcessorOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ErrorFileUciIdentifierPattern))
        {
            throw new InvalidOperationException($"{ProcessorOptions.SectionName}:{nameof(ProcessorOptions.ErrorFileUciIdentifierPattern)} is not configured.");
        }

        _pattern = new Regex(options.ErrorFileUciIdentifierPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        if (!_pattern.GetGroupNames().Contains(GroupName))
        {
            throw new InvalidOperationException($"{nameof(ProcessorOptions.ErrorFileUciIdentifierPattern)} must define a named group '{GroupName}'.");
        }
    }

    /// <param name="segmentsOutsideUci">Error-file text before and after the embedded UCI, searched separately.</param>
    public bool TryExtract(IEnumerable<string> segmentsOutsideUci, out string? identifier, out string? failure)
    {
        identifier = null;
        var values = segmentsOutsideUci
            .SelectMany(s => _pattern.Matches(s).Select(m => m.Groups[GroupName].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (values.Count == 0)
        {
            failure = "No UCI identifier was found in the error file.";
            return false;
        }
        if (values.Count > 1)
        {
            failure = "Conflicting UCI identifiers were found in the error file.";
            return false;
        }
        if (!SafeIdentifier().IsMatch(values[0]))
        {
            failure = "The UCI identifier is malformed.";
            return false;
        }

        identifier = values[0];
        failure = null;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeIdentifier();
}
