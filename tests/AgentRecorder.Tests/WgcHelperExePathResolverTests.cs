using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-WindowBackend")]
public sealed class WgcHelperExePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-recorder-wgc-resolver-" + Guid.NewGuid().ToString("N"));

    public WgcHelperExePathResolverTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for Windows file handles held by the test host.
        }
    }

    [Fact]
    public void ExplicitEnvironmentPath_TakesPriorityOverPortableAndDevelopmentCandidates()
    {
        string baseDirectory = CreateDirectory("package", "AgentRecorder.App");
        string envPath = CreateFile("explicit", WgcHelperExePathResolver.ExeName);
        CreateFile("package", WgcHelperExePathResolver.PortableRelativeDir, WgcHelperExePathResolver.ExeName);

        string? resolved = CreateResolver(baseDirectory, envPath).Resolve();

        Assert.Equal(Path.GetFullPath(envPath), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void InvalidExplicitEnvironmentPath_FailsClosedWithoutPortableFallback()
    {
        string baseDirectory = CreateDirectory("package", "AgentRecorder.App");
        CreateFile("package", WgcHelperExePathResolver.PortableRelativeDir, WgcHelperExePathResolver.ExeName);
        string invalidOverride = Path.Combine(_root, "missing", WgcHelperExePathResolver.ExeName);

        var exception = Assert.Throws<FileNotFoundException>(
            () => CreateResolver(baseDirectory, invalidOverride).Resolve());

        Assert.DoesNotContain(_root, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppDirectoryCandidate_IsResolvedAsAbsoluteRegularFile()
    {
        string baseDirectory = CreateDirectory("package", "AgentRecorder.App");
        string helper = CreateFile(
            "package",
            "AgentRecorder.App",
            WgcHelperExePathResolver.PortableRelativeDir,
            WgcHelperExePathResolver.ExeName);

        string resolved = CreateResolver(baseDirectory, null).Resolve();

        Assert.Equal(Path.GetFullPath(helper), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void AppAndHeadlessDirectories_ResolveSharedParentHelper()
    {
        string helper = CreateFile("package", WgcHelperExePathResolver.PortableRelativeDir, WgcHelperExePathResolver.ExeName);

        foreach (string appName in new[] { "AgentRecorder.App", "AgentRecorder.Headless" })
        {
            string baseDirectory = CreateDirectory("package", appName);
            string resolved = CreateResolver(baseDirectory, null).Resolve();
            Assert.Equal(Path.GetFullPath(helper), resolved);
            Assert.True(Path.IsPathFullyQualified(resolved));
        }
    }

    [Fact]
    public void ChangingCurrentDirectory_DoesNotChangePortableResolution()
    {
        string baseDirectory = CreateDirectory("package", "AgentRecorder.App");
        string helper = CreateFile("package", WgcHelperExePathResolver.PortableRelativeDir, WgcHelperExePathResolver.ExeName);
        string firstCwd = CreateDirectory("unrelated", "one");
        string secondCwd = CreateDirectory("unrelated", "two");
        string originalCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(firstCwd);
            string first = CreateResolver(baseDirectory, null).Resolve();
            Directory.SetCurrentDirectory(secondCwd);
            string second = CreateResolver(baseDirectory, null).Resolve();

            Assert.Equal(Path.GetFullPath(helper), first);
            Assert.Equal(first, second);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void DevelopmentRepositoryRootAndDefaultCandidate_AreDiscoveredFromBaseDirectory()
    {
        string repositoryRoot = CreateDirectory("repo");
        File.WriteAllText(Path.Combine(repositoryRoot, "AgentRecorder.sln"), "synthetic marker");
        string helper = CreateFile("repo", "tools", "wgc-native-helper", "bin", WgcHelperExePathResolver.ExeName);
        string baseDirectory = CreateDirectory("repo", "src", "AgentRecorder.App", "bin", "Release", "net8.0");

        string resolved = CreateResolver(baseDirectory, null).Resolve();

        Assert.Equal(Path.GetFullPath(helper), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void DirectoryPretendingToBeExecutable_IsRejected()
    {
        string baseDirectory = CreateDirectory("package", "AgentRecorder.App");
        Directory.CreateDirectory(Path.Combine(
            baseDirectory,
            WgcHelperExePathResolver.PortableRelativeDir,
            WgcHelperExePathResolver.ExeName));

        Assert.Throws<FileNotFoundException>(() => CreateResolver(baseDirectory, null).Resolve());
    }

    [Fact]
    public void ReparsePointIdentity_IsRejectedByInjectedFileIdentity()
    {
        string baseDirectory = CreateDirectory("package", "AgentRecorder.App");
        string helper = CreateFile("package", "AgentRecorder.App", WgcHelperExePathResolver.ExeName);
        string fullHelper = Path.GetFullPath(helper);

        var resolver = new WgcHelperExePathResolver.Resolver(
            baseDirectory,
            _ => null,
            path => path.Equals(fullHelper, StringComparison.OrdinalIgnoreCase)
                ? new WgcHelperExePathResolver.WgcHelperFileIdentity(path, true, false, true)
                : ReadIdentity(path));

        Assert.Throws<FileNotFoundException>(() => resolver.Resolve());
    }

    [Fact]
    public void ResolverFailure_ProducesStableProbeReasonAndFfmpegFallback()
    {
        var probe = new WgcContinuousAvailabilityProbe(
            () => throw new FileNotFoundException("synthetic missing helper"),
            new NeverCalledWgcRunner());
        var config = new CaptureConfig
        {
            SourceKind = "display",
            Bounds = (0, 0, 1280, 720),
            DurationSeconds = 5,
            Fps = 30,
        };

        string? previous = Environment.GetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, "wgc-continuous");
            var selection = CaptureBackendSelector.SelectWithEvidence(config, probe);

            Assert.Equal("ffmpeg", selection.BackendType);
            Assert.Equal("helper_missing", selection.Evidence.SelectionReasonCode);
            Assert.True(selection.Evidence.Fallback);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaptureBackendSelector.DisplayBackendEnvVar, previous);
        }
    }

    [Fact]
    public void PortableReleaseScript_RequiresNativeBuildTestsAndBoundedVersionSmoke()
    {
        string repositoryRoot = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-portable-release.ps1"));

        Assert.Contains("AgentRecorder.WgcHelper", script, StringComparison.Ordinal);
        Assert.Contains("tools\\wgc-native-helper\\build-native.ps1", script, StringComparison.Ordinal);
        Assert.Contains("-Configuration", script, StringComparison.Ordinal);
        Assert.Contains("-Platform", script, StringComparison.Ordinal);
        Assert.Contains("-OutputExeDir", script, StringComparison.Ordinal);
        Assert.Contains("-TestTimeoutMs", script, StringComparison.Ordinal);
        Assert.Contains("$nativeBuildHeadroomMs", script, StringComparison.Ordinal);
        Assert.Contains("$nativeBuildTimeoutMs", script, StringComparison.Ordinal);
        Assert.Contains("--version", script, StringComparison.Ordinal);
        Assert.Contains("WaitForExit", script, StringComparison.Ordinal);
        Assert.Contains("taskkill.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConvertTo-WindowsCommandLineArgument", script, StringComparison.Ordinal);
        Assert.Contains("CommandLineToArgvW", script, StringComparison.Ordinal);
        Assert.Contains("ArgumentList", script, StringComparison.Ordinal);
        Assert.Contains("AgentRecorderPortableJob", script, StringComparison.Ordinal);
        Assert.Contains("JobObjectLimitKillOnJobClose", script, StringComparison.Ordinal);
        Assert.Contains("cleanup incomplete", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid candidate removed", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$TestArgumentQuoting", script, StringComparison.Ordinal);
        Assert.Contains("$TestProcessTree", script, StringComparison.Ordinal);
        Assert.Contains("$SimulateTaskkillFailure", script, StringComparison.Ordinal);
        Assert.Contains("$SimulateJobOwnershipFailure", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-SkipRunTests", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-SkipTests", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeBuildExternalOutputSynchronizesCanonicalDevelopmentHelper()
    {
        string repositoryRoot = FindRepositoryRoot();
        string scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "wgc-native-helper",
            "build-native.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-TestSynchronization");

        using Process process = Process.Start(startInfo)!;
        Assert.True(process.WaitForExit(30000), "helper synchronization test mode timed out");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("external OutputExeDir cannot leave canonical helper stale", output, StringComparison.Ordinal);
    }

    private WgcHelperExePathResolver.Resolver CreateResolver(string baseDirectory, string? environmentValue) =>
        new(baseDirectory, _ => environmentValue, ReadIdentity);

    private string CreateDirectory(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateFile(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic helper");
        return path;
    }

    private static WgcHelperExePathResolver.WgcHelperFileIdentity? ReadIdentity(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                return null;

            FileAttributes attributes = File.GetAttributes(path);
            return new WgcHelperExePathResolver.WgcHelperFileIdentity(
                path,
                true,
                (attributes & FileAttributes.Directory) != 0,
                (attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch
        {
            return null;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentRecorder.sln")))
                return directory.FullName;
            directory = directory.Parent!;
        }

        throw new DirectoryNotFoundException("AgentRecorder.sln was not found from the test base directory.");
    }

    private sealed class NeverCalledWgcRunner : IWgcHelperProcessRunner
    {
        public WgcHelperProcessResult Run(
            string fileName,
            IReadOnlyList<string> argumentList,
            int timeoutMs,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The helper process must not start when resolution fails.");
    }
}
