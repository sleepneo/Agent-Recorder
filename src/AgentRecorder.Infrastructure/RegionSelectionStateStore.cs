using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Persisted state of the last successful region selection.
/// </summary>
public record SelectedRegionState(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("display_id")] string? DisplayId,
    [property: JsonPropertyName("coordinate_space")] string? CoordinateSpace,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt,
    [property: JsonPropertyName("source")] string? Source)
{
    public const int MinRegionSize = 32;

    /// <summary>
    /// A valid state must be marked available and have usable dimensions,
    /// a display identifier, and a coordinate space.
    /// </summary>
    public bool IsValid =>
        Available &&
        Width >= MinRegionSize &&
        Height >= MinRegionSize &&
        !string.IsNullOrWhiteSpace(DisplayId) &&
        !string.IsNullOrWhiteSpace(CoordinateSpace);
}

/// <summary>
/// Persists the last selected region to a JSON file in the data directory.
/// Writes are atomic (temp file + move) to avoid half-written files on crash.
/// </summary>
public static class RegionSelectionStateStore
{
    public static string StatePath =>
        Path.Combine(DataDirResolver.Resolve(), "state", "last-selected-region.json");

    public static SelectedRegionState? Load()
    {
        try
        {
            var path = StatePath;
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<SelectedRegionState>(json);
            return state?.IsValid ?? false ? state : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(SelectedRegionState state)
    {
        if (state == null || !state.IsValid)
            return;

        var path = StatePath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        File.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

        File.Move(tempPath, path, overwrite: true);
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(StatePath))
                File.Delete(StatePath);
        }
        catch
        {
            // ignored
        }
    }
}
