using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Verifies that the bundled FFmpeg binary accepts the same progress options
/// used by <see cref="Capture.FfmpegCaptureBackend.BuildArgs"/>. Uses a built-in lavfi
/// test source so no desktop capture, display, or UI is required.
/// </summary>
public class BundledFfmpegCompatibilityTests : IDisposable
{
    private readonly string _tmpDir;
    private Process? _proc;

    public BundledFfmpegCompatibilityTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"ffmpeg-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.Kill(true); } catch { }
            }
        }
        catch { }

        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void BundledFfmpeg_AcceptsNostatsAndProgressPipe()
    {
        var expectedBundledPath = Path.GetFullPath(Path.Combine(TestHelper.FfmpegBinDir, "ffmpeg.exe"));
        Assert.True(File.Exists(expectedBundledPath), $"Bundled FFmpeg not found at {expectedBundledPath}. This test requires the portable FFmpeg binary bundled in tools\\ffmpeg\\bin.");

        var outputPath = Path.Combine(_tmpDir, "compat.mp4");
        var args = $"-y -nostats -progress pipe:1 -f lavfi -i testsrc=duration=0.5:size=320x240:rate=30 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -t 0.5 \"{outputPath}\"";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = expectedBundledPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            },
            EnableRaisingEvents = true
        };

        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        var started = _proc.Start();
        Assert.True(started, "FFmpeg process should start");
        Assert.Equal(expectedBundledPath, Path.GetFullPath(_proc.StartInfo.FileName));

        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        const int timeoutMs = 15000;
        var exited = _proc.WaitForExit(timeoutMs);

        // Drain any remaining async stdout/stderr events after the process exits so the
        // last progress=end line is reliably captured.
        if (exited)
        {
            _proc.WaitForExit();
        }
        else
        {
            try { _proc.Kill(true); } catch { }
        }

        _proc.CancelOutputRead();
        _proc.CancelErrorRead();

        Assert.True(exited, $"FFmpeg should exit within {timeoutMs} ms");
        Assert.True(_proc.HasExited, "FFmpeg process should be in exited state");
        Assert.Equal(0, _proc.ExitCode);

        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();

        Assert.DoesNotContain("Unrecognized option", stderrText);
        Assert.DoesNotContain("Option not found", stderrText);
        Assert.Contains("progress=end", stdoutText);
    }
}
