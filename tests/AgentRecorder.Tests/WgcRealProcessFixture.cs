using System;
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
            return RunChild();
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

        for (int i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) break;
            switch (args[i])
            {
                case "--begin-signal": beginSignalPath = args[++i]; break;
                case "--begin-token": beginToken = args[++i]; break;
                case "--output": outputPath = args[++i]; break;
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
                    Arguments = "--child-sleep",
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

    private static int RunChild()
    {
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
            throw new InvalidOperationException(
                $"dotnet publish for WGC test helper failed (exit {proc.ExitCode}).{Environment.NewLine}" +
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
