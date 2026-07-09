using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using ApiException = AgentRecorder.Infrastructure.ApiException;
namespace AgentRecorder.Capture;

public sealed class FfmpegCaptureBackend : ICaptureBackend
{
    private Process? _proc;
    private string _output = "";
    private CaptureConfig? _cfg;
    private readonly StringBuilder _stderrLog = new();
    private readonly object _lock = new();
    private Task? _watcher;
    private OutputMeta? _completionMeta;
    private bool _hasExited = false;
    private bool _manualStopped = false;

    public void Start(CaptureConfig cfg)
    {
        _cfg = cfg;
        _output = cfg.OutputPath;

        var dir = Path.GetDirectoryName(_output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var args = BuildArgs(cfg);
        cfg.CommandArgs = args;

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegLocator.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
                RedirectStandardInput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                lock (_lock) _stderrLog.AppendLine(e.Data);
        };

        try { _proc.Start(); }
        catch (Exception ex)
        {
            throw new ApiException(500, "ENCODER_ERROR", "Failed to launch ffmpeg: " + ex.Message);
        }
        _proc.BeginErrorReadLine();

        int timeoutMs;
        if (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
            timeoutMs = (cfg.DurationSeconds.Value + 15) * 1000;
        else
            timeoutMs = 4 * 3600 * 1000;

        _watcher = Task.Run(() =>
        {
            bool exited = _proc.WaitForExit(timeoutMs);
            lock (_lock) _hasExited = true;
            int exitCode = -1;
            try { if (exited) exitCode = _proc.ExitCode; } catch { }

            if (!exited)
            {
                try { _proc.Kill(true); } catch { }
            }

            var meta = Probe(_output);
            string stderr;
            lock (_lock) stderr = _stderrLog.ToString();
            meta.StderrLog = stderr;
            _completionMeta = meta;

            lock (_lock)
            {
                if (_manualStopped) return;
            }
            _onNaturalExit?.Invoke(exitCode, meta);
        });
    }

    private Action<int, OutputMeta>? _onNaturalExit;

    public void OnNaturalExit(Action<int, OutputMeta> cb) => _onNaturalExit = cb;

    public string GetStderrLog()
    {
        lock (_lock) return _stderrLog.ToString();
    }

    public OutputMeta Stop()
    {
        string stderr;
        int exitCode = -1;

        lock (_lock)
        {
            _manualStopped = true;
            stderr = _stderrLog.ToString();
        }

        if (_proc != null && !_proc.HasExited)
        {
            try
            {
                _proc.StandardInput.Write('q');
                _proc.StandardInput.Flush();
                if (!_proc.WaitForExit(8000))
                {
                    try { _proc.Kill(true); } catch { }
                }
            }
            catch { try { _proc?.Kill(true); } catch { } }
        }
        try { if (_proc != null && _proc.HasExited) exitCode = _proc.ExitCode; } catch { }

        var meta = Probe(_output);
        meta.StderrLog = stderr;
        _completionMeta = meta;
        _hasExited = true;
        return meta;
    }

    public bool HasExited => _proc?.HasExited ?? _hasExited;
    public OutputMeta? LastMeta => _completionMeta;
    public int ExitCode
    {
        get
        {
            try { if (_proc != null && _proc.HasExited) return _proc.ExitCode; } catch { }
            return -1;
        }
    }

    private static string BuildArgs(CaptureConfig cfg)
    {
        var crf = cfg.Quality switch { "high" => 18, "low" => 28, _ => 23 };
        var sb = new StringBuilder("-y ");

        if (cfg.SourceKind == "window")
        {
            AppendGdigrabRegionArgs(sb, cfg);
        }
        else if (cfg.SourceKind == "region")
        {
            AppendGdigrabRegionArgs(sb, cfg);
        }
        else
        {
            var (x, y, w, h) = cfg.Bounds;
            if (w > 0 && h > 0)
                sb.Append($"-f gdigrab -framerate {cfg.Fps} -offset_x {x} -offset_y {y} -video_size {w}x{h} ");
            else
                sb.Append($"-f gdigrab -framerate {cfg.Fps} ");
            if (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
                sb.Append($"-t {cfg.DurationSeconds.Value} ");
            sb.Append("-i desktop ");
        }

        if (cfg.Microphone)
        {
            var dev = string.IsNullOrEmpty(cfg.MicDevice) ? "default" : cfg.MicDevice;
            sb.Append($"-f dshow -i audio=\"{dev}\" ");
        }

        var (_, _, capW, capH) = cfg.Bounds;
        if (cfg.SourceKind == "display" && (capW > 1920 || capH > 1080))
        {
            sb.Append("-vf \"scale=1920:1080:force_original_aspect_ratio=decrease\" ");
        }
        sb.Append($"-c:v libx264 -preset veryfast -crf {crf} -pix_fmt yuv420p -threads 4 ");
        if (cfg.Microphone) sb.Append("-c:a aac -b:a 128k ");

        sb.Append($"-movflags +faststart \"{cfg.OutputPath}\"");
        return sb.ToString();
    }

    private static void AppendGdigrabRegionArgs(StringBuilder sb, CaptureConfig cfg)
    {
        var (x, y, w, h) = cfg.Bounds;
        sb.Append($"-f gdigrab -framerate {cfg.Fps} ");
        if (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
            sb.Append($"-t {cfg.DurationSeconds.Value} ");
        sb.Append($"-offset_x {x} -offset_y {y} -video_size {w}x{h} ");
        sb.Append("-i desktop ");
    }

    public static OutputMeta Probe(string path)
    {
        var m = new OutputMeta();
        try
        {
            var fi = new FileInfo(path);
            m.SizeBytes = fi.Exists ? fi.Length : 0;
        }
        catch { }

        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = FfmpegLocator.FfprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            });
            if (p != null)
            {
                var json = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var root = JsonNode.Parse(json);
                m.DurationSeconds = double.TryParse(
                    root?["format"]?["duration"]?.GetValue<string>(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
                var vs = root?["streams"]?.AsArray()
                    .FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "video");
                if (vs != null)
                {
                    m.Width = vs["width"]?.GetValue<int>() ?? 0;
                    m.Height = vs["height"]?.GetValue<int>() ?? 0;
                    var fr = vs["r_frame_rate"]?.GetValue<string>() ?? "30/1";
                    var parts = fr.Split('/');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var den) && den != 0)
                        m.Fps = (int)Math.Round(double.Parse(parts[0]) / den);
                }
            }
        }
        catch { }
        return m;
    }

    public void Dispose() { try { _proc?.Dispose(); } catch { } }
}
