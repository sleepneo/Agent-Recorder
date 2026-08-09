using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Tests;

/// <summary>
/// Generates and caches a minimal real WGC helper executable for tests that
/// need a live subprocess tree. The helper is compiled on first use and reused
/// from a temp cache keyed by the combined hash of helper source and project
/// configuration.
/// </summary>
internal static class WgcRealProcessFixture
{
    private static readonly Lazy<Task<string>> HelperExePathLazy = new(() => CompileHelperAsync());

    /// <summary>
    /// Returns the path to a compiled WGC test helper EXE. The result is cached
    /// per AppDomain so multiple tests reuse the same build.
    /// </summary>
    public static Task<string> GetHelperExePathAsync() => HelperExePathLazy.Value;

    /// <summary>
    /// Source code of the helper. It parses a subset of WGC helper arguments,
    /// waits for the begin signal, emits a valid STARTED event, spawns an
    /// independently surviving child process, and writes a ready file that
    /// contains both the parent and child process IDs.
    ///
    /// After ready evidence is written, both the parent and child processes
    /// block indefinitely. They do not respond to stop signals, stdin EOF,
    /// Job Object close, or each other's exit. The only way they terminate is
    /// by an explicit external kill, which is exactly what the real process
    /// tree tests are meant to verify.
    /// </summary>
    private const string HelperSource = """
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AgentRecorder.WgcTestHelper;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child-sleep")
        {
            return RunChild(args);
        }

        if (args.Length > 0 && args[0] == "--grandchild-sleep")
        {
            return RunGrandchild();
        }

        return RunMain(args);
    }

    private static void Log(string message)
    {
        try
        {
            string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string logPath = Path.Combine(baseDir, "wgc-helper.log");
            string line = $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, line);
        }
        catch { }
    }

    private static int RunMain(string[] args)
    {
        string? beginSignalPath = null;
        string? beginToken = null;
        string? outputPath = null;
        // Optional tree depth: 2 = parent+child (default), 3 = also a
        // grandchild spawned by the child. Used by the deterministic
        // kill-tree timeout tests; default behavior is unchanged.
        int treeDepth = 2;

        for (int i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) break;
            switch (args[i])
            {
                case "--begin-signal": beginSignalPath = args[++i]; break;
                case "--begin-token": beginToken = args[++i]; break;
                case "--output": outputPath = args[++i]; break;
                case "--tree-depth": int.TryParse(args[++i], out treeDepth); break;
            }
        }

        string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        Log($"Started. PID={Environment.ProcessId}. Args={string.Join(" ", args)}");

        if (beginSignalPath == null || beginToken == null || outputPath == null)
        {
            Log("Missing required arguments");
            Console.Error.WriteLine("Missing required arguments");
            return 1;
        }

        // Wait for begin signal so the session passes authorization.
        Log($"Waiting for begin signal: {beginSignalPath}");
        if (!WaitForBeginSignal(beginSignalPath, beginToken, TimeSpan.FromSeconds(30)))
        {
            Log("Begin signal not received");
            Console.Error.WriteLine("Begin signal not received");
            return 2;
        }

        Log("Begin signal received");

        // Create a non-empty output file so the session can probe it if needed.
        try
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, new byte[1024]);
            Log($"Wrote output file: {outputPath}");
        }
        catch (Exception ex)
        {
            Log($"Failed to write output file: {ex}");
        }

        // Emit STARTED event so the session reaches Running state.
        Console.WriteLine("RESULT: STARTED");
        Console.WriteLine("RecordingId: wgc-test-helper");
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine("Container: mp4");
        Console.WriteLine("Codec: h264");
        Console.WriteLine("Fps: 30");
        Console.WriteLine("Width: 1920");
        Console.WriteLine("Height: 1080");
        Console.WriteLine("CaptureMethod: WGC_TEST_HELPER");
        Console.WriteLine("EncoderMode: software");
        Console.WriteLine("EncoderSelectionReason: software_default");
        Console.WriteLine();
        Console.Out.Flush();
        Log("Wrote STARTED event");

        // Spawn a child process to verify process-tree kill.
        Process? child = null;
        string? childExePath = null;
        string parentBaseName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "wgc-helper";
        string childBaseName = parentBaseName + "-child";

        try
        {
            string currentExe = Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
            {
                childExePath = Path.Combine(baseDir, childBaseName + ".exe");
                File.Copy(currentExe, childExePath, overwrite: true);

                child = Process.Start(new ProcessStartInfo
                {
                    FileName = childExePath,
                    Arguments = "--child-sleep --tree-depth " + treeDepth,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Log($"Spawned child PID={child?.Id}");
            }
            else
            {
                Log("Could not determine current exe path; no child spawned.");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to spawn child: {ex}");
        }

        // The child must be alive before we claim readiness. Otherwise the test
        // could pass even though it never verified a real process-tree kill.
        if (child == null || child.HasExited)
        {
            Log("Child process was not spawned or exited early; refusing to write ready evidence.");
            return 3;
        }

        // Write ready evidence so the test knows both processes are stable.
        string readyPath = Path.Combine(baseDir, "wgc-helper-ready.signal");
        try
        {
            string readyContent = $"{{\"parentPid\":{Environment.ProcessId},\"childPid\":{child.Id}}}";
            File.WriteAllText(readyPath, readyContent);
            Log($"Wrote ready file: {readyPath} content={readyContent}");
        }
        catch (Exception ex)
        {
            Log($"Failed to write ready file: {ex}");
            return 4;
        }

        // Block forever. The helper must not exit on stop signal, stdin EOF,
        // Job Object close, or any other side effect of the parent process.
        // Only an explicit external kill terminates it, which is what the test
        // is designed to prove happens via the product's process tree cleanup.
        Log("Blocking indefinitely; only an explicit kill should terminate this process.");
        try
        {
            Thread.Sleep(Timeout.Infinite);
        }
        catch { }

        return 0;
    }

    private static int RunChild(string[] args)
    {
        // Optional third tree level: with --tree-depth >= 3 the child spawns a
        // grandchild copy of itself and publishes both PIDs in a deterministic
        // ASCII signal file before blocking, so kill-tree tests can verify all
        // three levels by exact PID.
        int treeDepth = 2;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--tree-depth")
                int.TryParse(args[i + 1], out treeDepth);
        }

        if (treeDepth >= 3)
        {
            try
            {
                string currentExe = Environment.ProcessPath ?? string.Empty;
                string baseDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
                string parentBaseName = Path.GetFileNameWithoutExtension(currentExe) ?? "wgc-helper-child";
                string grandchildExePath = Path.Combine(baseDir, parentBaseName + "-grandchild.exe");

                if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                {
                    File.Copy(currentExe, grandchildExePath, overwrite: true);
                    var grandchild = Process.Start(new ProcessStartInfo
                    {
                        FileName = grandchildExePath,
                        Arguments = "--grandchild-sleep",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });

                    if (grandchild != null && !grandchild.HasExited)
                    {
                        string signalPath = Path.Combine(baseDir, "wgc-helper-grandchild.signal");
                        File.WriteAllText(signalPath,
                            $"{{\"childPid\":{Environment.ProcessId},\"grandchildPid\":{grandchild.Id}}}");
                        Log($"Spawned grandchild PID={grandchild.Id}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to spawn grandchild: {ex}");
            }
        }

        // Keep the child alive indefinitely. It must not depend on the parent
        // stdin, parent pipe, parent exit, Job Object close, stop signal, or
        // any temporary file disappearing. Only an explicit kill should
        // terminate it.
        try
        {
            Thread.Sleep(Timeout.Infinite);
        }
        catch { }
        return 0;
    }

    private static int RunGrandchild()
    {
        // Deepest tree level: block indefinitely; only an explicit external
        // kill (via the root's process-tree kill) terminates it.
        try
        {
            Thread.Sleep(Timeout.Infinite);
        }
        catch { }
        return 0;
    }

    private static bool WaitForBeginSignal(string path, string token, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() == token)
                    return true;
            }
            catch { }
            Thread.Sleep(50);
        }
        return false;
    }
}
""";

    private static string GetCsproj() => """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <DebugType>none</DebugType>
  </PropertyGroup>
</Project>
""";

    private static async Task<string> CompileHelperAsync()
    {
        string source = HelperSource;
        string csproj = GetCsproj();
        string hash = ComputeHash(source + csproj);
        string cacheDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", "wgc-helper-cache", hash);
        string publishDir = Path.Combine(cacheDir, "publish");
        string exePath = Path.Combine(publishDir, "AgentRecorder.WgcTestHelper.exe");

        if (File.Exists(exePath))
            return exePath;

        Directory.CreateDirectory(cacheDir);

        string csPath = Path.Combine(cacheDir, "Program.cs");
        string csprojPath = Path.Combine(cacheDir, "AgentRecorder.WgcTestHelper.csproj");

        await File.WriteAllTextAsync(csPath, source);
        await File.WriteAllTextAsync(csprojPath, GetCsproj());

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{csprojPath}\" -c Release -r win-x64 --self-contained false " +
                        $"/p:PublishSingleFile=true /p:PublishReadyToRun=false " +
                        $"/p:IncludeNativeLibrariesForSelfExtract=false /p:DebugType=none " +
                        $"-o \"{publishDir}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start dotnet publish for WGC test helper.");

        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            // `dotnet publish` implicitly restores, which is impossible on an
            // offline machine or wherever `dotnet restore` itself is broken
            // (NuGet.targets(745,5) "Value cannot be null (Parameter 'path1')").
            // The helper only needs the BCL, so fall back to a restore-free
            // compile: Roslyn csc against the reference pack plus apphost
            // stamping. The layout (apphost exe + same-name dll +
            // runtimeconfig.json) resolves through the embedded dll path, so
            // the helper's runtime self-copy for the child process works too.
            string? fallbackError = null;
            try
            {
                CompileOfflineFallback(csPath, publishDir, exePath);
            }
            catch (Exception fallbackEx)
            {
                fallbackError = fallbackEx.ToString();
            }

            if (File.Exists(exePath))
                return exePath;

            throw new InvalidOperationException(
                $"dotnet publish for WGC test helper failed (exit {proc.ExitCode}), " +
                $"and the restore-free offline fallback failed: {fallbackError}{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{stderr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{stdout}");
        }

        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException(
                $"Helper EXE not found after publish: {exePath}.{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{stdout}{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{stderr}");
        }

        return exePath;
    }

    // -----------------------------------------------------------------
    // Restore-free offline fallback (used when `dotnet restore` cannot run)
    // -----------------------------------------------------------------

    private static void CompileOfflineFallback(string csPath, string publishDir, string exePath)
    {
        string dotnetExe = FindDotnetExe()
            ?? throw new InvalidOperationException("dotnet.exe not found (PATH/DOTNET_ROOT/Program Files) for offline fallback.");
        string dotnetRoot = Path.GetDirectoryName(dotnetExe)!;

        string cscDll = NewestVersionedFile(
            Path.Combine(dotnetRoot, "sdk"), "Roslyn/bincore/csc.dll", major: 8);
        string refDir = NewestVersionedDirectory(
            Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref"), "ref/net8.0", major: 8);
        string apphostPath = NewestVersionedFile(
            Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Host.win-x64"),
            "runtimes/win-x64/native/apphost.exe", major: 8);
        string runtimeVersion = NewestVersionedDirectory(
            Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App"), marker: null, major: 8);

        const string dllName = "AgentRecorder.WgcTestHelper.dll";
        string dllPath = Path.Combine(publishDir, dllName);
        Directory.CreateDirectory(publishDir);

        var args = new StringBuilder();
        args.Append("exec \"").Append(cscDll).Append("\" /nologo /nostdlib /noconfig /target:exe")
            .Append(" /nullable:enable /debug- /out:\"").Append(dllPath).Append("\" \"")
            .Append(csPath).Append('"');
        foreach (var refDll in Directory.GetFiles(refDir, "*.dll"))
            args.Append(" /r:\"").Append(refDll).Append('"');

        var cscPsi = new ProcessStartInfo
        {
            FileName = dotnetExe,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var cscProc = Process.Start(cscPsi)
            ?? throw new InvalidOperationException("Failed to start Roslyn csc for offline fallback.");
        string cscOut = cscProc.StandardOutput.ReadToEnd();
        string cscErr = cscProc.StandardError.ReadToEnd();
        cscProc.WaitForExit();
        if (cscProc.ExitCode != 0 || !File.Exists(dllPath))
        {
            throw new InvalidOperationException(
                $"Roslyn csc failed (exit {cscProc.ExitCode}).{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{cscErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{cscOut}");
        }

        File.WriteAllText(Path.Combine(publishDir, "AgentRecorder.WgcTestHelper.runtimeconfig.json"),
            """
            {
              "runtimeOptions": {
                "tfm": "net8.0",
                "framework": { "name": "Microsoft.NETCore.App", "version": "__VER__" },
                "configProperties": { "System.Globalization.Invariant": true }
              }
            }
            """.Replace("__VER__", runtimeVersion));

        // Stamp the ABSOLUTE dll path, not the bare dll name. The real-process
        // tests copy only the exe into a renamed staging location (and the
        // helper self-copies its exe for the child process); an absolute
        // embedded path lets every such copy resolve the dll and its sibling
        // runtimeconfig.json from this publish cache directory, reproducing
        // the relocation semantics of PublishSingleFile without the bundler.
        StampAppHost(apphostPath, exePath, dllPath);
    }

    private static string? FindDotnetExe()
    {
        var candidates = new List<string>();

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
            candidates.Add(Path.Combine(dotnetRoot, "dotnet.exe"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try { candidates.Add(Path.Combine(dir, "dotnet.exe")); } catch { }
        }

        // Well-known install locations (the shell may resolve "dotnet" via
        // App Paths even when Program Files is not on the test host PATH).
        try
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe"));
        }
        catch { }

        foreach (var candidate in candidates)
        {
            try { if (File.Exists(candidate)) return candidate; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Picks the newest &lt;major&gt;.x version directory under <paramref name="root"/>
    /// that contains <paramref name="marker"/> (a relative file or directory),
    /// and returns the matching file/directory path.
    /// </summary>
    private static string NewestVersionedFile(string root, string marker, int major)
        => NewestVersionedDirectory(root, marker, major, wantFile: true);

    private static string NewestVersionedDirectory(string root, string? marker, int major, bool wantFile = false)
    {
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"Offline fallback component directory missing: {root}");

        string? bestPath = null;
        Version? bestVersion = null;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!Version.TryParse(Path.GetFileName(dir), out var version) || version.Major != major)
                continue;

            string candidate = marker == null
                ? Path.GetFileName(dir) // shared-runtime: the version string itself
                : Path.Combine(dir, marker.Replace('/', Path.DirectorySeparatorChar));

            bool exists = marker == null
                ? true
                : (wantFile ? File.Exists(candidate) : Directory.Exists(candidate));
            if (!exists)
                continue;

            if (bestVersion == null || version > bestVersion)
            {
                bestVersion = version;
                bestPath = candidate;
            }
        }

        return bestPath ?? throw new InvalidOperationException(
            $"No {major}.x versioned component found under {root} (marker: {marker ?? "<version>"}).");
    }

    /// <summary>
    /// Stamps an apphost template with the managed dll path, mirroring
    /// Microsoft.NET.HostModel AppHost.HostWriter: the template embeds a
    /// 64-character placeholder at the start of a 1024-byte reserved window
    /// that is overwritten with the UTF-8 dll path, zero-padded.
    /// </summary>
    private static void StampAppHost(string apphostTemplatePath, string destExePath, string appDllName)
    {
        const string placeholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
        const int reservedWindow = 1024;

        byte[] bytes = File.ReadAllBytes(apphostTemplatePath);
        byte[] marker = Encoding.ASCII.GetBytes(placeholder);
        int index = IndexOf(bytes, marker);
        if (index < 0)
            throw new InvalidOperationException("apphost placeholder not found in template.");

        byte[] pathBytes = Encoding.UTF8.GetBytes(appDllName);
        if (pathBytes.Length + 1 > reservedWindow)
            throw new InvalidOperationException("app dll name exceeds the apphost reserved window.");

        Array.Clear(bytes, index, reservedWindow);
        Array.Copy(pathBytes, 0, bytes, index, pathBytes.Length);
        File.WriteAllBytes(destExePath, bytes);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        byte[] hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
