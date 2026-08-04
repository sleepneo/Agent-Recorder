using System.IO;

namespace AgentRecorder.Capture;

/// <summary>
/// Resolves the native WGC helper without making the portable layout depend on
/// the process working directory.
/// </summary>
public static class WgcHelperExePathResolver
{
    public const string EnvVarName = "AGENT_RECORDER_WGC_HELPER_EXE";
    public const string ExeName = "wgc-native-helper.exe";
    public const string PortableRelativeDir = "AgentRecorder.WgcHelper";

    public static string Resolve()
    {
        var resolver = new Resolver(
            AppContext.BaseDirectory,
            name => Environment.GetEnvironmentVariable(name),
            ResolveFileIdentity);
        return resolver.Resolve();
    }

    /// <summary>
    /// Parameterized resolver used by tests and by the static production entry
    /// point. The seams are instance-owned so tests cannot mutate production
    /// process-wide state.
    /// </summary>
    internal sealed class Resolver
    {
        private readonly string _baseDirectory;
        private readonly Func<string, string?> _environmentReader;
        private readonly Func<string, WgcHelperFileIdentity?> _fileIdentityReader;

        public Resolver(
            string baseDirectory,
            Func<string, string?> environmentReader,
            Func<string, WgcHelperFileIdentity?> fileIdentityReader)
        {
            _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
            _environmentReader = environmentReader ?? throw new ArgumentNullException(nameof(environmentReader));
            _fileIdentityReader = fileIdentityReader ?? throw new ArgumentNullException(nameof(fileIdentityReader));
        }

        public string Resolve()
        {
            string? fromEnv = _environmentReader(EnvVarName)?.Trim();
            if (!string.IsNullOrEmpty(fromEnv))
            {
                string? overridden = TryResolveCandidate(fromEnv);
                if (overridden != null)
                    return overridden;

                // An explicit override is fail-closed: never silently run a
                // different helper when the requested binary is invalid.
                throw new FileNotFoundException(
                    $"The WGC helper override {EnvVarName} is not a valid regular file.");
            }

            foreach (string candidate in GetPortableCandidates())
            {
                string? resolved = TryResolveCandidate(candidate);
                if (resolved != null)
                    return resolved;
            }

            string? repositoryRoot = FindRepositoryRoot(_baseDirectory);
            if (repositoryRoot != null)
            {
                foreach (string candidate in GetDevelopmentCandidates(repositoryRoot))
                {
                    string? resolved = TryResolveCandidate(candidate);
                    if (resolved != null)
                        return resolved;
                }
            }

            throw new FileNotFoundException(
                "WGC helper executable was not found. Checked the explicit override, " +
                "portable package layout, and development workspace candidates.");
        }

        private IEnumerable<string> GetPortableCandidates()
        {
            if (string.IsNullOrWhiteSpace(_baseDirectory))
                yield break;

            yield return Path.Combine(_baseDirectory, PortableRelativeDir, ExeName);
            yield return Path.Combine(_baseDirectory, ExeName);

            string? parent = Directory.GetParent(_baseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))?.FullName;
            if (!string.IsNullOrEmpty(parent))
                yield return Path.Combine(parent, PortableRelativeDir, ExeName);
        }

        private static IEnumerable<string> GetDevelopmentCandidates(string repositoryRoot)
        {
            yield return Path.Combine(
                repositoryRoot,
                "tools",
                "wgc-native-helper",
                "bin",
                ExeName);
            yield return Path.Combine(
                repositoryRoot,
                "tools",
                "wgc-native-helper",
                "bin",
                "x64",
                "Release",
                ExeName);
            yield return Path.Combine(
                repositoryRoot,
                "tools",
                "wgc-native-helper",
                "bin",
                "x64",
                "Debug",
                ExeName);
        }

        private string? TryResolveCandidate(string candidate)
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                var identity = _fileIdentityReader(fullPath);
                if (identity == null
                    || !identity.Exists
                    || identity.IsDirectory
                    || identity.IsReparsePoint)
                {
                    return null;
                }

                return fullPath;
            }
            catch
            {
                return null;
            }
        }

        private static string? FindRepositoryRoot(string startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
                return null;

            try
            {
                var directory = new DirectoryInfo(startDirectory);
                while (directory != null)
                {
                    if (directory.EnumerateFiles("AgentRecorder.sln").Any())
                        return directory.FullName;
                    directory = directory.Parent;
                }
            }
            catch
            {
                // A broken or inaccessible base directory simply has no dev
                // workspace candidate.
            }

            return null;
        }
    }

    internal sealed record WgcHelperFileIdentity(
        string FullPath,
        bool Exists,
        bool IsDirectory,
        bool IsReparsePoint);

    private static WgcHelperFileIdentity? ResolveFileIdentity(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return null;

            FileAttributes attributes = File.GetAttributes(fullPath);
            return new WgcHelperFileIdentity(
                fullPath,
                Exists: true,
                IsDirectory: (attributes & FileAttributes.Directory) != 0,
                IsReparsePoint: (attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch
        {
            return null;
        }
    }
}
