using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit.Sdk;

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
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "AgentRecorderTests",
            $"wgc-t48-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        TestDirectoryCleanup.DeleteOwnedDirectory(Path);
    }
}

internal static class TestDirectoryCleanup
{
    public static void DeleteOwnedDirectory(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path))
                    return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }

            Thread.Sleep(50 * (attempt + 1));
        }

        throw new XunitException(
            $"Failed to clean owned test directory '{path}' after bounded retries. Last error: {last}");
    }

    public static void DeleteOwnedDirectoryAndEmptyParent(string path, string parentPath)
    {
        DeleteOwnedDirectory(path);

        if (!Directory.Exists(parentPath))
            return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(parentPath).Any())
                Directory.Delete(parentPath);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // A sibling may have appeared concurrently; never delete or retry
            // a parent that is no longer provably empty.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the parent's ownership boundary when it cannot be read.
        }
    }
}
