using System.Text.RegularExpressions;
using Gfs.Console.Uci.Retry.Configuration;
using Gfs.Console.Uci.Retry.Models;

namespace Gfs.Console.Uci.Retry.Processing;

/// <summary>
/// GeoCode only when every processing failure in the error wrapper is an exact message of the form:
/// "NN*NNN*NNNN {GeoCodeErrorIdentifier} NNNNN[-NNNN]. It cannot be entered on a new asset location for Asset ID N."
/// </summary>
/// <remarks>
/// A UCI with several assets can report one such message per asset, so more than one match is expected. The wrapper
/// before the UCI is a header (blank lines and space-free lines such as the file name) followed by 0xFD-delimited fields in any order. Each non-empty field must be an exact GeoCode message, a number, or a
/// "{contract} Contract not created." field (exactly one, anywhere), with at least one GeoCode message. Any other field
/// is an unrecognized failure, so the file is Unknown (manual review) rather than guessed at.
/// Without any 0xFD delimiter the structure cannot be proven, so exactly one message is required.
/// </remarks>
public sealed class ErrorClassifier
{
    private const char FieldDelimiter = '\u00FD';
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Header = new(@"^(?:[ \t]*\r?\n|\S+\r?\n)*\z", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Number = new(@"^[ \t]*\d+\s*\z", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Contract = new(@"^[ \t]*\S+ Contract not created\.\s*\z", RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex FirstLine = new(@"^\s*[^\r\n]*", RegexOptions.CultureInvariant, Timeout);

    private readonly Regex _geoCode;
    private readonly Regex _geoCodeLine;

    public ErrorClassifier(ProcessorOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.GeoCodeErrorIdentifier))
        {
            throw new InvalidOperationException($"{ProcessorOptions.SectionName}:{nameof(ProcessorOptions.GeoCodeErrorIdentifier)} is not configured.");
        }

        _geoCode = new Regex(
            @"(?<![\w*])\d{2}\*\d{3}\*\d{4} " + Regex.Escape(options.GeoCodeErrorIdentifier.Trim())
            + @" \d{5}(?:-\d{4})?\.[ \t]+It cannot be entered on a new asset location for Asset ID \d+\.",
            RegexOptions.CultureInvariant,
            Timeout);
        _geoCodeLine = new Regex(@"^[ \t]*" + _geoCode.ToString()[@"(?<![\w*])".Length..] + @"\s*\z", RegexOptions.CultureInvariant, Timeout);
    }

    /// <param name="errorText">Error-file content outside the embedded UCI.</param>
    public UciErrorType Classify(string errorText)
    {
        var fields = errorText.Split(FieldDelimiter);
        if (fields.Length == 1)
        {
            return _geoCode.Count(errorText) == 1 ? UciErrorType.GeoCode : UciErrorType.Unknown;
        }

        var geoCodeMessages = 0;
        var contractFields = 0;
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (i == 0)
            {
                var lineStart = field.LastIndexOf('\n') + 1;
                if (!Header.IsMatch(field[..lineStart])) return UciErrorType.Unknown;
                field = field[lineStart..];
            }

            if (i == fields.Length - 1)
            {
                // Text after the final field's line (the UCI, when it could not be extracted) is not wrapper content.
                field = FirstLine.Match(field).Value;
            }

            if (_geoCodeLine.IsMatch(field)) geoCodeMessages++;
            else if (Contract.IsMatch(field)) contractFields++;
            else if (!Number.IsMatch(field) && field.Trim().Length > 0) return UciErrorType.Unknown;
        }

        return geoCodeMessages > 0 && contractFields == 1 ? UciErrorType.GeoCode : UciErrorType.Unknown;
    }}
