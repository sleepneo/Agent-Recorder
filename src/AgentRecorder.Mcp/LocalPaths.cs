using System.Text.RegularExpressions;

namespace AgentRecorder.Mcp;

internal static partial class LocalPaths
{
    // Deliberately accept drive-rooted local paths only. Reject ambiguity before GetFullPath
    // can erase traversal, trailing characters, device names or alternate data streams.
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || !DriveRoot().IsMatch(value) ||
            value.Any(c => c < 32 || "<>\"|?*".Contains(c)) || value[2..].Contains(':'))
            throw new ContractException("path_unsupported");
        var parts = value[3..].Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || DeviceName().IsMatch(p)))
            throw new ContractException("path_unsupported");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new ContractException("path_unsupported");
        for (string? cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new ContractException("path_reparse_point");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    public static bool Equal(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool Contains(string parent, string child)
    {
        parent = Normalize(parent);
        child = Normalize(child);
        return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z]:[\\\\/]")]
    private static partial Regex DriveRoot();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\\.)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceName();
}
