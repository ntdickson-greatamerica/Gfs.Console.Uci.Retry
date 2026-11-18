namespace Gfs.Console.Uci.Retry;

/// <summary>Parses and validates the single supported argument: <c>--error-file "&lt;full path&gt;"</c>.</summary>
public static class CommandLine
{
    public const string ErrorFileArgument = "--error-file";

    /// <returns>The full error-file path, or null with <paramref name="error"/> set.</returns>
    public static string? TryGetErrorFile(string[] args, out string? error)
    {
        error = null;

        var occurrences = args.Count(a => string.Equals(a, ErrorFileArgument, StringComparison.OrdinalIgnoreCase));
        if (occurrences == 0)
        {
            error = $"Required argument {ErrorFileArgument} was not supplied.";
            return null;
        }
        if (occurrences > 1)
        {
            error = $"Argument {ErrorFileArgument} was supplied more than once.";
            return null;
        }
        if (args.Length != 2 || !string.Equals(args[0], ErrorFileArgument, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Expected exactly: {ErrorFileArgument} \"<full path>\".";
            return null;
        }

        var value = args[1];
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Argument {ErrorFileArgument} must not be empty.";
            return null;
        }

        string fullPath;
        try
        {
            if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(value))
            {
                error = $"Argument {ErrorFileArgument} must be a valid, fully qualified path.";
                return null;
            }
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Argument {ErrorFileArgument} is not a valid path.";
            return null;
        }

        if (Directory.Exists(fullPath))
        {
            error = "The error-file path is a directory, not a file.";
            return null;
        }
        if (!File.Exists(fullPath))
        {
            error = "The error file does not exist.";
            return null;
        }

        return fullPath;
    }
}
