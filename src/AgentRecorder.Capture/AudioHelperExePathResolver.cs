using System.IO;

namespace AgentRecorder.Capture;

/// <summary>
/// Resolves the path to the AgentRecorder.AudioHelper executable.
/// Priority:
/// 1) AGENT_RECORDER_AUDIO_HELPER_EXE environment variable
/// 2) Portable layout relative to AppContext.BaseDirectory (AgentRecorder.AudioHelper\AgentRecorder.AudioHelper.exe)
/// 3) Development workspace relative to solution/repository root
/// 4) Project build output relative to the executing assembly location
/// Throws if none of the candidates is a regular file.
/// </summary>
public static class AudioHelperExePathResolver
{
    public const string EnvVarName = "AGENT_RECORDER_AUDIO_HELPER_EXE";
    public const string ExeName = "AgentRecorder.AudioHelper.exe";
    public const string PortableRelativeDir = "AgentRecorder.AudioHelper";

    public static string Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName)?.Trim();
        if (!string.IsNullOrEmpty(fromEnv))
        {
            var canonical = CanonicalizeFile(fromEnv);
            if (canonical != null)
                return canonical;

            throw new FileNotFoundException(
                $"Audio helper executable specified by {EnvVarName} was not found.",
                fromEnv);
        }

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var portable = Path.Combine(baseDir, PortableRelativeDir, ExeName);
            var canonical = CanonicalizeFile(portable);
            if (canonical != null)
                return canonical;

            // Self-contained publish places helper next to main executable.
            var sibling = Path.Combine(baseDir, ExeName);
            canonical = CanonicalizeFile(sibling);
            if (canonical != null)
                return canonical;

            // Portable layout: App/Headless/Cli each live in their own subdirectory
            // under the package root, with a single shared AgentRecorder.AudioHelper
            // directory next to them.
            var parentDir = Path.GetDirectoryName(baseDir);
            if (!string.IsNullOrEmpty(parentDir))
            {
                var sharedPortable = Path.Combine(parentDir, PortableRelativeDir, ExeName);
                canonical = CanonicalizeFile(sharedPortable);
                if (canonical != null)
                    return canonical;
            }
        }

        // Development/workspace fallback: walk up from BaseDirectory looking for
        // the repository root marker (AgentRecorder.sln) and then the project output.
        var repoRoot = FindRepositoryRoot(baseDir);
        if (!string.IsNullOrEmpty(repoRoot))
        {
            var projectOutput = Path.Combine(repoRoot, "tools", "AgentRecorder.AudioHelper", "bin", "Release", "net8.0-windows10.0.19041.0", ExeName);
            var canonical = CanonicalizeFile(projectOutput);
            if (canonical != null)
                return canonical;

            var debugOutput = Path.Combine(repoRoot, "tools", "AgentRecorder.AudioHelper", "bin", "Debug", "net8.0-windows10.0.19041.0", ExeName);
            canonical = CanonicalizeFile(debugOutput);
            if (canonical != null)
                return canonical;
        }

        throw new FileNotFoundException(
            "Audio helper executable not found. " +
            $"Set {EnvVarName} or ensure {ExeName} is published next to the application " +
            $"(portable layout: {PortableRelativeDir}\\{ExeName}).",
            fromEnv ?? ExeName);
    }

    /// <summary>
    /// Test seam: attempts to resolve without throwing and returns null if not found.
    /// </summary>
    public static string? TryResolve()
    {
        try
        {
            return Resolve();
        }
        catch
        {
            return null;
        }
    }

    private static string? CanonicalizeFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists)
                return null;
            // Must be a regular file (not a directory pretending to be a file, and
            // reject reparse points for the executable itself).
            if ((info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                return null;
            if ((info.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindRepositoryRoot(string? startDir)
    {
        if (string.IsNullOrEmpty(startDir))
            return null;

        try
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (dir.EnumerateFiles("AgentRecorder.sln").Any())
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
