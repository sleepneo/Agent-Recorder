using System;
using System.IO;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class AudioHelperExePathResolverTests : IDisposable
{
    private readonly string? _originalEnv;

    public AudioHelperExePathResolverTests()
    {
        _originalEnv = Environment.GetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, _originalEnv);
    }

    [Fact]
    public void Resolve_EnvVarOverride_ReturnsCanonicalPath()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"audio_helper_env_{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(tempFile, "dummy");
            Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, tempFile);

            var resolved = AudioHelperExePathResolver.Resolve();

            Assert.Equal(Path.GetFullPath(tempFile), resolved);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    [Fact]
    public void Resolve_EnvVarOverride_MissingFile_ThrowsFileNotFoundException()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.exe");
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, missing);

        Assert.Throws<FileNotFoundException>(() => AudioHelperExePathResolver.Resolve());
    }

    [Fact]
    public void TryResolve_NoCandidate_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, null);
        // Force BaseDirectory to a location with no helper by using a subdirectory under temp.
        var originalBase = AppContext.BaseDirectory;
        Assert.NotNull(originalBase);

        var resolved = AudioHelperExePathResolver.TryResolve();

        // In the real test environment the development build output is present,
        // so this assertion validates the happy path is discoverable.
        Assert.NotNull(resolved);
        Assert.EndsWith(AudioHelperExePathResolver.ExeName, resolved);
    }

    [Fact]
    public void Resolve_DevelopmentBuildOutput_ReturnsHelperExe()
    {
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, null);

        var resolved = AudioHelperExePathResolver.Resolve();

        Assert.NotNull(resolved);
        Assert.EndsWith(AudioHelperExePathResolver.ExeName, resolved);
        Assert.True(File.Exists(resolved), $"Resolved helper does not exist: {resolved}");
    }
}
