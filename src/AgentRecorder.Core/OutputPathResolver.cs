using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Security;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Core;

/// <summary>
/// Shared helper for building and adjusting recording output paths.
/// Centralises directory validation, filename generation, and conflict resolution
/// so that both ConfigParser and the confirmation-time path override use the same logic.
/// </summary>
internal static class OutputPathResolver
{
    /// <summary>
    /// Checks request syntax without reading defaults, enumerating devices, creating
    /// directories, or probing existing files. Runtime conflict checks remain separate.
    /// </summary>
    public static void ValidateRequest(JsonNode? output)
    {
        if (Str(output?["directory"]) is { } directory && directory != "default")
            CanonicalizeDirectory(directory);
        // Template substitutions are safe single-component values. Validate through
        // the same expansion and filename rules used when constructing the real path.
        _ = BuildFileName(output, new Recording("validation") { SourceTitle = "region" });
    }

    // Validate the original spelling before lexical normalization. Only local
    // absolute DOS paths enter ordinary output binding; this never resolves links
    // or substitutes a persistent publication/identity check.
    internal static string CanonicalizeDirectory(string directory)
    {
        PolicyEngine.ValidateDirectory(directory);
        try
        {
            // GetFullPath may trim trailing dots/spaces. Do not turn an unsafe
            // original component into an accepted name while normalizing separators.
            var originalRoot = Path.GetPathRoot(directory)!;
            if (originalRoot.Length != 3 || !char.IsAsciiLetter(originalRoot[0]) || originalRoot[1] != ':')
                throw InvalidPath("output.directory");
            foreach (var part in directory[originalRoot.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                if (part is not ("." or "..")) ValidateName(part, "output.directory");
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full)!;
            if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != Path.DirectorySeparatorChar)
                throw InvalidPath("output.directory");
            PolicyEngine.ValidateDirectory(full);
            foreach (var part in full[root.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                ValidateName(part, "output.directory");
            if (directory.Any(c => char.IsControl(c) || "\"<>|?*".Contains(c))) throw InvalidPath("output.directory");
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath("output.directory");
        }
    }

    private static string ValidateName(string name, string field)
    {
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || Path.IsPathRooted(name) ||
            name.Any(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                "123456789¹²³".Contains(stem[3])))
            throw InvalidPath(field);
        return name;
    }

    private static ApiException InvalidPath(string field) => new(400, "OUTPUT_PATH_INVALID",
        "Output must use a valid absolute directory and a single valid filename.", new { field });

    public static string BuildScreenshotSeriesPath(JsonNode? output, Recording rec)
    {
        var directory = Str(output?["directory"]) is { } d && d != "default"
            ? d
            : Paths.DefaultOutputDir;
        directory = CanonicalizeDirectory(directory);
        var template = Str(output?["filename_template"]) ?? "screenshot-series-{datetime}-{id}";
        var name = BuildSafeScreenshotSeriesName(ApplyTemplate(template, rec));
        return Path.Combine(directory, name);
    }

    public static string MoveScreenshotSeriesToDirectory(string existingPath, string directory, Recording rec)
    {
        directory = CanonicalizeDirectory(directory);
        var name = Path.GetFileName(existingPath);
        if (string.IsNullOrWhiteSpace(name))
            name = ApplyTemplate("screenshot-series-{datetime}-{id}", rec);
        name = BuildSafeScreenshotSeriesName(name);
        return Path.Combine(directory, name);
    }

    private static string BuildSafeScreenshotSeriesName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            Path.IsPathRooted(name) ||
            name is "." or ".." ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar) ||
            Path.GetFileName(name) != name ||
            name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "Screenshot-series filename_template must resolve to one safe directory name.",
                new
                {
                    field = "output.filename_template",
                    reason = "path_traversal_or_invalid_name"
                });
        }

        return name;
    }
    /// <summary>
    /// Builds the final output path from the request's output node and recording metadata.
    /// </summary>
    public static string BuildOutputPath(JsonNode? output, Recording rec, bool createDirectory = true)
    {
        ValidateRequest(output);
        string dir = Str(output?["directory"]) is { } d && d != "default"
            ? d : Paths.DefaultOutputDir;
        dir = CanonicalizeDirectory(dir);
        string name = BuildFileName(output, rec);
        if (createDirectory) Directory.CreateDirectory(dir);
        var policy = Str(output?["conflict_policy"]) ?? "rename";
        var full = Path.Combine(dir, name);
        rec.OrdinaryRequestedOutputPath = full;
        return ResolveConflict(full, policy);
    }

    /// <summary>
    /// Moves an already-built output path into a different directory, keeping the same filename.
    /// The target directory is validated and created if necessary. Conflicts are resolved
    /// according to <paramref name="conflictPolicy"/> (default "rename").
    /// </summary>
    public static string MoveToDirectory(string existingOutputPath, string directory, string conflictPolicy = "rename", bool createDirectory = true)
    {
        if (string.IsNullOrWhiteSpace(existingOutputPath))
            throw new ArgumentException("Existing output path cannot be empty", nameof(existingOutputPath));
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory cannot be empty", nameof(directory));

        directory = CanonicalizeDirectory(directory);
        if (createDirectory) Directory.CreateDirectory(directory);

        var name = Path.GetFileName(existingOutputPath);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Existing output path has no file name", nameof(existingOutputPath));

        var full = Path.Combine(directory, name);
        return ResolveConflict(full, conflictPolicy);
    }

    /// <summary>
    /// Resolves an output path conflict according to the given policy.
    /// Supported policies: "rename" (default), "fail", "overwrite".
    /// </summary>
    public static string ResolveConflict(string fullPath, string policy)
    {
        if (!File.Exists(fullPath)) return fullPath;

        switch (policy)
        {
            case "fail":
                throw new ApiException(409, "OUTPUT_PATH_INVALID", "Output file already exists");
            case "overwrite":
                throw new ApiException(403, "PERMISSION_DENIED", "Overwrite requires explicit confirmation");
            default:
                var dir = Path.GetDirectoryName(fullPath)!;
                var stem = Path.GetFileNameWithoutExtension(fullPath);
                var ext = Path.GetExtension(fullPath);
                for (int i = 1; ; i++)
                {
                    var cand = Path.Combine(dir, $"{stem}-{i}{ext}");
                    if (!File.Exists(cand)) return cand;
                }
        }
    }

    private static string BuildFileName(JsonNode? output, Recording rec)
    {
        if (Str(output?["filename"]) is { } fn)
        {
            ValidateName(fn, "output.filename");
            return ValidateName(fn.EndsWith(".mp4") ? fn : fn + ".mp4", "output.filename");
        }

        var tmpl = Str(output?["filename_template"]) ?? "recording-{datetime}-{id}";
        var name = ValidateName(ApplyTemplate(tmpl, rec), "output.filename_template");
        return ValidateName(name + ".mp4", "output.filename_template");
    }

    private static string ApplyTemplate(string t, Recording rec)
    {
        var now = DateTime.Now;
        return t.Replace("{date}", now.ToString("yyyy-MM-dd"))
                .Replace("{time}", now.ToString("HHmmss"))
                .Replace("{datetime}", now.ToString("yyyy-MM-dd-HHmmss"))
                .Replace("{source}", Sanitize(rec.SourceTitle))
                .Replace("{id}", rec.Id);
    }

    private static string Sanitize(string s) =>
        new string(s.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());

    private static string? Str(JsonNode? n) => n?.GetValue<string>();
}
