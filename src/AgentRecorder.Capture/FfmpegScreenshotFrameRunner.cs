using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentRecorder.Capture;

/// <summary>
/// The exact finite command used for one screenshot-series frame. Keeping the
/// argument list separate from process ownership makes the capture contract
/// directly testable without launching a desktop capture process.
/// </summary>
internal static class FfmpegScreenshotFrameCommand
{
    // gdigrab samples at a finite, low-latency cadence while -frames:v 1 still
    // bounds the invocation to one encoded PNG. This is not a continuous worker.
    internal const int InputFrameRate = 30;

    internal static ProcessStartInfo BuildStartInfo(CaptureConfig config, string tempPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in BuildArguments(config, tempPath))
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    internal static IReadOnlyList<string> BuildArguments(CaptureConfig config, string tempPath)
    {
        var (x, y, w, h) = config.Bounds;
        return new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "gdigrab", "-framerate", InputFrameRate.ToString(CultureInfo.InvariantCulture),
            "-offset_x", x.ToString(CultureInfo.InvariantCulture),
            "-offset_y", y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{w}x{h}",
            "-i", "desktop",
            "-c:v", "png",
            "-frames:v", "1",
            "-f", "image2",
            tempPath
        };
    }
}

/// <summary>
/// Production single-frame runner. Each frame gets a fresh, bounded FFmpeg
/// process, so the screenshot-series path never opens a continuous recorder.
/// </summary>
public sealed class FfmpegScreenshotFrameRunner : IScreenshotFrameRunner
{
    private readonly Func<ProcessStartInfo, IScreenshotFrameProcess> _processFactory;

    public FfmpegScreenshotFrameRunner()
        : this(static startInfo => new SystemScreenshotFrameProcess(startInfo))
    {
    }

    internal FfmpegScreenshotFrameRunner(Func<ProcessStartInfo, IScreenshotFrameProcess> processFactory)
        => _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));

    public async Task<ScreenshotFrameResult> CaptureAsync(
        ScreenshotFrameRequest request,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var cfg = request.Config;
        if (!IsSupportedRequest(request, out var planError))
        {
            return new ScreenshotFrameResult(false, planError, 0, 0, 0,
                started, DateTime.UtcNow, -1);
        }

        var psi = FfmpegScreenshotFrameCommand.BuildStartInfo(cfg, request.TempPath);
        using var process = _processFactory(psi);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        try
        {
            process.Start();
            var stderrTask = process.ReadStandardErrorToEndAsync(timeout.Token);
            var stdoutTask = process.ReadStandardOutputToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return new ScreenshotFrameResult(false, "frame_capture_failed", 0, 0, 0,
                    started, DateTime.UtcNow, process.ExitCode);
            }

            return new ScreenshotFrameResult(true, "", 0, 0,
                File.Exists(request.TempPath) ? new FileInfo(request.TempPath).Length : 0,
                started, DateTime.UtcNow, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            await TryKillAndReapAsync(process).ConfigureAwait(false);
            return new ScreenshotFrameResult(false,
                cancellationToken.IsCancellationRequested ? "capture_cancelled" : "frame_timeout",
                0, 0, 0, started, DateTime.UtcNow, -1);
        }
        catch
        {
            await TryKillAndReapAsync(process).ConfigureAwait(false);
            return new ScreenshotFrameResult(false, "frame_capture_failed", 0, 0, 0,
                started, DateTime.UtcNow, -1);
        }
    }

    private static bool IsSupportedRequest(ScreenshotFrameRequest request, out string errorCode)
    {
        errorCode = "unsupported_capture_plan";
        if (request.Config == null || !request.Config.IsScreenshotSeries || request.Config.AudioRequested ||
            string.IsNullOrWhiteSpace(request.TempPath) || request.Config.Bounds.w <= 0 || request.Config.Bounds.h <= 0)
            return false;

        if (!string.Equals(request.BackendType, "ffmpeg-single-frame", StringComparison.Ordinal))
            return false;

        string expectedSemantics = request.SourceKind switch
        {
            "display" => "display_surface",
            "region" => "region_rectangle",
            "window" => "screen_rectangle",
            _ => ""
        };
        if (expectedSemantics.Length == 0 ||
            !string.Equals(request.CaptureSemantics, expectedSemantics, StringComparison.Ordinal))
            return false;

        if (!string.Equals(request.CoordinateSpace, "virtual_screen", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static async Task TryKillAndReapAsync(IScreenshotFrameProcess process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { }
    }
}

/// <summary>
/// Narrow process seam for deterministic screenshot-runner lifecycle tests.
/// </summary>
internal interface IScreenshotFrameProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    void Start();
    Task WaitForExitAsync(CancellationToken cancellationToken);
    Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken);
    Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

internal sealed class SystemScreenshotFrameProcess : IScreenshotFrameProcess
{
    private const int MaxOutputChars = 64 * 1024;
    private readonly Process _process;

    public SystemScreenshotFrameProcess(ProcessStartInfo startInfo)
    {
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public void Start()
    {
        if (!_process.Start())
            throw new InvalidOperationException("The screenshot process reported an unsuccessful start.");
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => _process.WaitForExitAsync(cancellationToken);

    public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
        => ReadBoundedAsync(_process.StandardError, cancellationToken);

    public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
        => ReadBoundedAsync(_process.StandardOutput, cancellationToken);

    public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
    public void Dispose() => _process.Dispose();

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(capacity: Math.Min(MaxOutputChars, buffer.Length));
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length >= MaxOutputChars)
                continue;

            int keep = Math.Min(read, MaxOutputChars - output.Length);
            output.Append(buffer, 0, keep);
        }

        return output.ToString();
    }
}
