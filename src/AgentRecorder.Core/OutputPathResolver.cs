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
    /// Builds the final output path from the request's output node and recording metadata.
    /// </summary>
    public static string BuildOutputPath(JsonNode? output, Recording rec)
    {
        string dir = Str(output?["directory"]) is { } d && d != "default"
            ? d : Paths.DefaultOutputDir;
        PolicyEngine.ValidateDirectory(dir);
        Directory.CreateDirectory(dir);

        string name = BuildFileName(output, rec);
        var policy = Str(output?["conflict_policy"]) ?? "rename";
        var full = Path.Combine(dir, name);
        return ResolveConflict(full, policy);
    }

    /// <summary>
    /// Moves an already-built output path into a different directory, keeping the same filename.
    /// The target directory is validated and created if necessary. Conflicts are resolved
    /// according to <paramref name="conflictPolicy"/> (default "rename").
    /// </summary>
    public static string MoveToDirectory(string existingOutputPath, string directory, string conflictPolicy = "rename")
    {
        if (string.IsNullOrWhiteSpace(existingOutputPath))
            throw new ArgumentException("Existing output path cannot be empty", nameof(existingOutputPath));
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory cannot be empty", nameof(directory));

        PolicyEngine.ValidateDirectory(directory);
        Directory.CreateDirectory(directory);

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
            return fn.EndsWith(".mp4") ? fn : fn + ".mp4";

        var tmpl = Str(output?["filename_template"]) ?? "recording-{datetime}";
        return ApplyTemplate(tmpl, rec) + ".mp4";
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
