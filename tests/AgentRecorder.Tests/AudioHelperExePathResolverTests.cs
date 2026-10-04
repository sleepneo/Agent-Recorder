using System;
using System.IO;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderEnvVar")]
public sealed class AudioHelperExePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"Task308 helper 空格 {Guid.NewGuid():N}");
    private readonly string? _originalEnv;

    public AudioHelperExePathResolverTests()
    {
        _originalEnv = Environment.GetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName);
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, null);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, _originalEnv);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("AgentRecorder.App")]
    [InlineData("AgentRecorder.Headless")]
    [InlineData("AgentRecorder.Cli")]
    public void ResolveFromPortableSibling_FindsSharedHelper_WithOrWithoutTrailingSeparator(string appFolder)
    {
        var appDirectory = Path.Combine(_root, appFolder);
        Directory.CreateDirectory(appDirectory);
        var helper = CreateHelper(Path.Combine(_root, AudioHelperExePathResolver.PortableRelativeDir));

        Assert.Equal(helper, AudioHelperExePathResolver.ResolveFromBaseDirectory(appDirectory, allowDevelopmentFallback: false));
        Assert.Equal(helper, AudioHelperExePathResolver.ResolveFromBaseDirectory(
            appDirectory + Path.DirectorySeparatorChar, allowDevelopmentFallback: false));
    }

    [Fact]
    public void ResolveFromBaseDirectory_ValidEnvironmentOverridePrecedesPortableLayout()
    {
        var portable = CreateHelper(Path.Combine(_root, AudioHelperExePathResolver.PortableRelativeDir));
        var explicitHelper = CreateHelper(Path.Combine(_root, "explicit override"));
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, explicitHelper);

        Assert.Equal(Path.GetFullPath(explicitHelper), AudioHelperExePathResolver.ResolveFromBaseDirectory(
            Path.Combine(_root, "AgentRecorder.App"), allowDevelopmentFallback: false));
        Assert.NotEqual(portable, AudioHelperExePathResolver.ResolveFromBaseDirectory(
            Path.Combine(_root, "AgentRecorder.App"), allowDevelopmentFallback: false));
    }

    [Fact]
    public void InvalidEnvironmentOverride_IsRejectedWithoutPortableFallback()
    {
        CreateHelper(Path.Combine(_root, AudioHelperExePathResolver.PortableRelativeDir));
        var missing = Path.Combine(_root, "missing.exe");
        Environment.SetEnvironmentVariable(AudioHelperExePathResolver.EnvVarName, missing);

        var error = Assert.Throws<FileNotFoundException>(() => AudioHelperExePathResolver.ResolveFromBaseDirectory(
            Path.Combine(_root, "AgentRecorder.App"), allowDevelopmentFallback: false));
        Assert.Equal(missing, error.FileName);
    }

    [Fact]
    public void MissingCandidate_IsNotResolvedAndDoesNotSearchDevelopmentOutput()
    {
        var appDirectory = Path.Combine(_root, "AgentRecorder.App");
        Directory.CreateDirectory(appDirectory);

        Assert.Null(AudioHelperExePathResolver.TryResolveFromBaseDirectory(
            appDirectory + Path.DirectorySeparatorChar, allowDevelopmentFallback: false));
        Assert.Throws<FileNotFoundException>(() => AudioHelperExePathResolver.ResolveFromBaseDirectory(
            appDirectory + Path.DirectorySeparatorChar, allowDevelopmentFallback: false));
    }

    [Fact]
    public void DirectoryNamedAsExecutable_IsRejected()
    {
        var helperDirectory = Path.Combine(_root, AudioHelperExePathResolver.PortableRelativeDir);
        Directory.CreateDirectory(Path.Combine(helperDirectory, AudioHelperExePathResolver.ExeName));

        Assert.Throws<FileNotFoundException>(() => AudioHelperExePathResolver.ResolveFromBaseDirectory(
            Path.Combine(_root, "AgentRecorder.App"), allowDevelopmentFallback: false));
    }

    [Fact]
    public void ReparsePointHelper_IsRejectedWhenFileSymlinksAreAvailable()
    {
        var appDirectory = Path.Combine(_root, "AgentRecorder.App");
        Directory.CreateDirectory(appDirectory);
        var helperDirectory = Path.Combine(_root, AudioHelperExePathResolver.PortableRelativeDir);
        Directory.CreateDirectory(helperDirectory);
        var target = Path.Combine(_root, "actual.exe");
        File.WriteAllText(target, "synthetic helper");
        var link = Path.Combine(helperDirectory, AudioHelperExePathResolver.ExeName);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine($"REPARSE_TEST_NOT_EXERCISED: file symlink creation is unavailable: {ex.Message}");
            return;
        }

        Assert.Throws<FileNotFoundException>(() => AudioHelperExePathResolver.ResolveFromBaseDirectory(
            appDirectory, allowDevelopmentFallback: false));
    }

    private static string CreateHelper(string directory)
    {
        Directory.CreateDirectory(directory);
        var helper = Path.Combine(directory, AudioHelperExePathResolver.ExeName);
        File.WriteAllText(helper, "synthetic helper");
        return Path.GetFullPath(helper);
    }
}
