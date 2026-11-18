namespace Gfs.Console.Uci.Retry.Models;

/// <summary>
/// The UCI payload isolated from an error file. <see cref="Text"/> is the exact slice of the file, decoded with
/// <see cref="ErrorFileEncoding"/> (a lossless 1:1 byte/char mapping), so it must be written back with that encoding.
/// </summary>
public sealed class EmbeddedUci(string text, int startOffset, int endOffset)
{
    public string Text { get; } = text;

    /// <summary>Offset of the first character of the UCI within the decoded error file.</summary>
    public int StartOffset { get; } = startOffset;

    /// <summary>Offset just past the last character of the UCI within the decoded error file.</summary>
    public int EndOffset { get; } = endOffset;

    // Never expose payload content through logging or diagnostics.
    public override string ToString() => $"EmbeddedUci[length={Text.Length}]";
}

/// <summary>
/// The encoding used to turn error-file bytes into text and back.
/// </summary>
/// <remarks>
/// ISO-8859-1 is chosen for a technical reason, not because it is the business-defined source encoding (that is not
/// known): it maps every byte value to exactly one char and back. Character offsets are therefore byte offsets,
/// splices are byte-exact, and every byte outside an intended change survives the round trip, including bytes that
/// would be invalid or lossy in UTF-8/ASCII. Do not swap it for a "smarter" encoding without re-proving that property.
/// </remarks>
public static class ErrorFileEncoding
{
    /// <summary>ISO-8859-1 maps every byte to exactly one char, so slicing and re-encoding are byte-exact.</summary>
    public static readonly System.Text.Encoding Encoding = System.Text.Encoding.Latin1;

    public static string ReadAll(string path) => Encoding.GetString(File.ReadAllBytes(path));
}
