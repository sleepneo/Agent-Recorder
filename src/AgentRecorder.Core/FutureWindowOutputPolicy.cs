namespace AgentRecorder.Core;

/// <summary>Shared fail-closed preflight for the one frozen future-window output target.</summary>
internal static class FutureWindowOutputPolicy
{
    internal static string? ValidateAndProbe(string directory, string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filePath) ||
                !Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(filePath) ||
                !string.Equals(Path.GetFullPath(directory), directory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(filePath), filePath, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(directory) || File.Exists(filePath) ||
                !string.Equals(Path.GetDirectoryName(filePath), directory, StringComparison.OrdinalIgnoreCase))
                return "future_window_output_environment_changed";

            var current = Path.GetPathRoot(directory);
            if (string.IsNullOrWhiteSpace(current)) return "future_window_output_directory_invalid";
            foreach (var part in directory[current.Length..].Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return "future_window_output_directory_reparse_point";
            }

            var probe = Path.Combine(directory, ".agent-recorder-future-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.DeleteOnClose))
                stream.WriteByte(0x41);
            return null;
        }
        catch (UnauthorizedAccessException) { return "future_window_output_directory_unwritable"; }
        catch (IOException) { return "future_window_output_environment_unavailable"; }
        catch { return "future_window_output_environment_unavailable"; }
    }
}
