using System;
using System.IO;

namespace AgentRecorder.Tests;

internal static class TestHelper
{
    // Test assembly: tests/AgentRecorder.Tests/bin/.../AgentRecorder.Tests.dll
    // AppContext.BaseDirectory = tests/AgentRecorder.Tests/bin/Release/net8.0-windows10.0.19041.0/
    // Go up 5 levels: net8.0-windows10.0.19041.0 -> Release -> bin -> AgentRecorder.Tests -> tests -> project root
    public static readonly string ProjectRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string FfmpegBinDir => Path.Combine(ProjectRoot, "tools", "ffmpeg", "bin");
}

/// <summary>
/// Creates a unique temporary directory under the system temp path
/// and deletes it on disposal. Used to ensure WGC evidence fixtures
/// never leak to disk.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wgc-t48-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Intentionally ignored: best-effort cleanup.
        }
    }
}
