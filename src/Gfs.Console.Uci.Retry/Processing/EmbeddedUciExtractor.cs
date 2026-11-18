using Gfs.Console.Uci.Retry.Models;

namespace Gfs.Console.Uci.Retry.Processing;

/// <summary>
/// Isolates the UCI embedded in an error file without modifying it. Fails closed when boundaries are not unambiguous.
/// </summary>
/// <remarks>
/// An error file is wrapper/error text plus the original UCI that failed. The UCI starts at its CU header and the
/// wrapper marks its end with '{'. That '{' belongs to the wrapper, not to the UCI, so it is excluded here and must
/// never reach an outbound retry file. The extracted text is an exact slice of the decoded file (see
/// <see cref="ErrorFileEncoding"/>): nothing is trimmed, normalized or re-joined, because the corrected UCI is later
/// produced by splicing into this text and must differ from the original only in the intended fields.
/// <para>
/// Start: exactly one "CU|" at the start of the text or directly after CR/LF.
/// End: exactly one '{' after the start that is the last character of its physical line (followed by CR, LF or end of
/// text). That '{' is the wrapper terminator: the UCI is everything from CU up to, but excluding, it. Other '{'
/// inside the payload are data. Section structure is not judged here; <see cref="UciDocumentParser"/> does that.
/// </para>
/// </remarks>
public static class EmbeddedUciExtractor
{
    private const string StartToken = "CU|";
    private const char Terminator = '{';
    private const string Utf8BomAsLatin1 = "\u00EF\u00BB\u00BF";

    /// <summary>Returns false (with a non-sensitive <paramref name="failure"/>) unless there is exactly one start and one terminator.</summary>
    public static bool TryExtract(string text, out EmbeddedUci? uci, out string? failure)
    {
        uci = null;
        failure = null;

        var starts = FindAll(text, StartToken, 0, IsLineStart);
        if (starts.Count == 0)
        {
            failure = "No CU section start was found.";
            return false;
        }
        if (starts.Count > 1)
        {
            failure = "Multiple possible CU section starts were found.";
            return false;
        }

        var start = starts[0];
        var ends = new List<int>();
        for (var i = text.IndexOf(Terminator, start); i >= 0; i = text.IndexOf(Terminator, i + 1))
        {
            if (i + 1 == text.Length || text[i + 1] is '\r' or '\n')
            {
                ends.Add(i);
            }
        }
        if (ends.Count == 0)
        {
            failure = "No '{' UCI terminator at the end of a line was found after the CU section.";
            return false;
        }
        if (ends.Count > 1)
        {
            failure = "Multiple possible '{' UCI terminators were found.";
            return false;
        }

        uci = new EmbeddedUci(text[start..ends[0]], start, ends[0]);
        return true;
    }

    private static List<int> FindAll(string text, string token, int from, Func<string, int, bool> isBoundary)
    {
        var found = new List<int>();
        var i = from;
        while ((i = text.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            if (isBoundary(text, i))
            {
                found.Add(i);
            }
            i += token.Length;
        }
        return found;
    }

    // A UTF-8 BOM read through ISO-8859-1 shows up as three characters; it is file framing, not part of the UCI.
    private static bool IsLineStart(string text, int i) =>
        i == 0
        || text[i - 1] is '\r' or '\n'
        || (i == Utf8BomAsLatin1.Length && text.StartsWith(Utf8BomAsLatin1, StringComparison.Ordinal));
}
