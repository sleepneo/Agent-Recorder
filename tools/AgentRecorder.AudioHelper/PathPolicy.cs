namespace AgentRecorder.AudioHelper;

/// <summary>
/// Path containment policy for the audio helper. Mirrors the security rules
/// used by wgc-native-helper: absolute paths only, no traversal, no reparse
/// point escapes, strict extension checks, and writable-parent verification.
/// </summary>
internal sealed class PathPolicy
{
    private const int MaxPathLength = 32767;

    public string AllowedRoot { get; }

    public PathPolicy(string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot))
            throw new ArgumentException("Allowed root cannot be empty", nameof(allowedRoot));

        try
        {
            AllowedRoot = Path.GetFullPath(allowedRoot).Replace('/', '\\').TrimEnd('\\');
        }
        catch
        {
            AllowedRoot = allowedRoot.Replace('/', '\\').TrimEnd('\\');
        }
    }

    public PathCheckResult ValidateOutputPath(string outputPath)
    {
        var result = new PathCheckResult();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            result.Error = "Output path is empty";
            return result;
        }

        if (!IsSafePathString(outputPath))
        {
            result.Error = "Output path contains unsafe characters";
            return result;
        }

        if (!Path.IsPathRooted(outputPath))
        {
            result.Error = "Output path must be absolute";
            return result;
        }

        string full;
        try
        {
            full = Path.GetFullPath(outputPath);
        }
        catch
        {
            result.Error = "Output path is invalid";
            return result;
        }

        if (full.Length > MaxPathLength)
        {
            result.Error = "Output path is too long";
            return result;
        }

        if (!IsPathContained(full, AllowedRoot))
        {
            result.Error = "Output path must be under the allowed root";
            return result;
        }

        if (ContainsReparsePointEscape(full))
        {
            result.Error = "Output path contains a reparse point / symlink escape";
            return result;
        }

        if (IsSameCanonicalPath(full, AllowedRoot))
        {
            result.Error = "Output path must differ from allowed root";
            return result;
        }

        if (!string.Equals(Path.GetExtension(full), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            result.Error = "Output path must have .wav extension";
            return result;
        }

        string parent = Path.GetDirectoryName(full)!;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            result.Error = "Output parent directory does not exist";
            return result;
        }

        if (!IsDirectoryWritable(parent))
        {
            result.Error = "Output parent directory is not writable";
            return result;
        }

        if (File.Exists(full))
        {
            result.Error = "Output file already exists";
            return result;
        }

        string partialPath = BuildPartialPath(full);
        if (File.Exists(partialPath))
        {
            result.Error = "Partial output file already exists";
            return result;
        }

        result.CanonicalPath = full;
        result.PartialPath = partialPath;
        result.OpenPartialStream = () => OpenPartialStream(partialPath, AllowedRoot);
        result.Ok = true;
        return result;
    }

    public PathCheckResult ValidateStopSignalPath(string stopSignalPath, PathCheckResult? outputResult = null)
    {
        var result = new PathCheckResult();
        if (string.IsNullOrWhiteSpace(stopSignalPath))
        {
            result.Error = "Stop signal path is empty";
            return result;
        }

        if (!IsSafePathString(stopSignalPath))
        {
            result.Error = "Stop signal path contains unsafe characters";
            return result;
        }

        if (!Path.IsPathRooted(stopSignalPath))
        {
            result.Error = "Stop signal path must be absolute";
            return result;
        }

        string full;
        try
        {
            full = Path.GetFullPath(stopSignalPath);
        }
        catch
        {
            result.Error = "Stop signal path is invalid";
            return result;
        }

        if (full.Length > MaxPathLength)
        {
            result.Error = "Stop signal path is too long";
            return result;
        }

        if (!IsPathContained(full, AllowedRoot))
        {
            result.Error = "Stop signal path must be under the allowed root";
            return result;
        }

        if (ContainsReparsePointEscape(full))
        {
            result.Error = "Stop signal path contains a reparse point / symlink escape";
            return result;
        }

        if (outputResult != null)
        {
            if (IsSameCanonicalPath(full, outputResult.CanonicalPath))
            {
                result.Error = "Stop signal path must differ from output path";
                return result;
            }

            if (IsSameCanonicalPath(full, outputResult.PartialPath))
            {
                result.Error = "Stop signal path must differ from partial output path";
                return result;
            }
        }

        string parent = Path.GetDirectoryName(full)!;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            result.Error = "Stop signal parent directory does not exist";
            return result;
        }

        if (!IsDirectoryWritable(parent))
        {
            result.Error = "Stop signal parent directory is not writable";
            return result;
        }

        result.CanonicalPath = full;
        result.Ok = true;
        return result;
    }

    private static string BuildPartialPath(string outputPath)
    {
        string dir = Path.GetDirectoryName(outputPath)!;
        string fileName = Path.GetFileNameWithoutExtension(outputPath)!;
        return Path.Combine(dir, $"{fileName}.{Environment.ProcessId}.partial.wav");
    }

    private static Stream OpenPartialStream(string partialPath, string allowedRoot)
    {
        string parent = Path.GetDirectoryName(partialPath)!;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            throw new IOException("Output parent directory does not exist");

        string full = Path.GetFullPath(partialPath);
        if (ContainsReparsePointEscape(full))
            throw new IOException("Partial output path contains a reparse point escape");

        if (!IsPathContained(full, allowedRoot))
            throw new IOException("Partial output path must be under the allowed root");

        if (!IsDirectoryWritable(parent))
            throw new IOException("Output parent directory is not writable");

        return new FileStream(full, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    private static bool IsSameCanonicalPath(string a, string b)
    {
        return string.Equals(
            a.Replace('/', '\\').TrimEnd('\\'),
            b.Replace('/', '\\').TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathContained(string child, string parent)
    {
        if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent))
            return false;

        string childLower = child.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        string parentLower = parent.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        if (parentLower.Length > childLower.Length)
            return false;

        if (!childLower.StartsWith(parentLower, StringComparison.Ordinal))
            return false;

        if (childLower.Length == parentLower.Length)
            return true;

        return childLower[parentLower.Length] == '\\';
    }

    private static bool IsSafePathString(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // Reject device paths.
        if (path.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
            return false;

        // Reject explicit \\?\ paths except \\?\UNC\.
        const string ntPrefix = "\\\\?\\";
        const string uncPrefix = "\\\\?\\UNC\\";
        int checkStart = 0;
        if (path.StartsWith(ntPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
                checkStart = uncPrefix.Length;
            else
                return false;
        }

        for (int i = checkStart; i < path.Length; i++)
        {
            char c = path[i];
            if (c < 32 || c == '*' || c == '?' || c == '<' || c == '>' || c == '|' || c == '"' || c == 0)
                return false;

            if (c == ':')
            {
                if (i != 1 || path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks existing path components and reports true if any component is a
    /// reparse point (symlink, junction, mount point). This prevents a path
    /// that is syntactically under the allowed root from resolving outside it.
    /// </summary>
    private static bool ContainsReparsePointEscape(string path)
    {
        try
        {
            string current = path.Replace('/', '\\').TrimEnd('\\');
            while (!string.IsNullOrEmpty(current) && current.Length > 3)
            {
                if (Directory.Exists(current))
                {
                    var info = new DirectoryInfo(current);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        return true;
                }
                else if (File.Exists(current))
                {
                    var info = new FileInfo(current);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        return true;
                }

                string parent = Path.GetDirectoryName(current)!;
                if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }
        catch
        {
            // Treat any inspection failure as a potential escape.
            return true;
        }

        return false;
    }

    private static bool IsDirectoryWritable(string directory)
    {
        string probe = Path.Combine(directory, $"audio_write_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            try { File.Delete(probe); } catch { }
            return false;
        }
    }
}

internal sealed class PathCheckResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public string CanonicalPath { get; set; } = "";
    public string PartialPath { get; set; } = "";
    public Func<Stream>? OpenPartialStream { get; set; }
}
