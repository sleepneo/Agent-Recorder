using System.Diagnostics;
using System.Threading;

namespace AgentRecorder.Capture;

/// <summary>
/// Builds arguments for wgc-native-helper.exe using the IPC contract
/// described in doc/wgc-helper-ipc-contract.md and (optionally) runs it
/// via an injectable IWgcHelperProcessRunner.
///
/// The runner is consumed by <c>WgcWindowCaptureBackend.Start()</c> for the
/// experimental still-frame backend (<c>container=png, codec=still-frame</c>).
/// It is NOT the default window recording pipeline; FFmpeg gdigrab remains the default.
/// </summary>
public sealed class WgcHelperRunner
{
    private const int MinTimeoutMs = 100;
    private const int MaxTimeoutMs = 30000;

    private readonly string _helperExePath;
    private readonly IWgcHelperProcessRunner _processRunner;

    public WgcHelperRunner(string helperExePath, IWgcHelperProcessRunner processRunner)
    {
        _helperExePath = string.IsNullOrWhiteSpace(helperExePath)
            ? throw new ArgumentException("Helper executable path must be provided.", nameof(helperExePath))
            : helperExePath;

        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    /// <summary>
    /// Build ProcessStartInfo with the required security properties
    /// (UseShellExecute=false, redirects, CreateNoWindow, ArgumentList).
    /// Exposed for tests and for inspection.
    /// </summary>
    public ProcessStartInfo BuildStartInfo(WgcHelperOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        var psi = new ProcessStartInfo
        {
            FileName = _helperExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };

        // Arguments (added via ArgumentList — never shell-joined).
        psi.ArgumentList.Add("--capture-one-frame-window");
        psi.ArgumentList.Add($"0x{options.Hwnd.ToInt64():X16}");
        psi.ArgumentList.Add("--i-understand-this-captures-screen"); // always present
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(options.OutputPath ?? string.Empty);
        psi.ArgumentList.Add("--timeout-ms");
        int clamped = Clamp(options.TimeoutMs, MinTimeoutMs, MaxTimeoutMs);
        psi.ArgumentList.Add(clamped.ToString());

        return psi;
    }

    /// <summary>
    /// Run the helper (through the injectable process runner) and return
    /// a structured WgcHelperResult.
    /// </summary>
    public WgcHelperResult Run(WgcHelperOptions options, CancellationToken cancellationToken = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        var psi = BuildStartInfo(options);

        // Materialize the argument list in stable order for the runner.
        var args = new List<string>(psi.ArgumentList.Count);
        foreach (var a in psi.ArgumentList) args.Add(a);

        int clamped = Clamp(options.TimeoutMs, MinTimeoutMs, MaxTimeoutMs);

        var proc = _processRunner.Run(psi.FileName, args, clamped, cancellationToken);

        return WgcHelperOutputParser.Parse(proc.ExitCode, proc.StandardOutput, proc.StandardError);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
