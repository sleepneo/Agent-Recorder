using System;
using System.IO;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;
namespace AgentRecorder.Capture;
public static class FfmpegLocator
{
    private const string FfmpegDirEnvVar = "AGENT_RECORDER_FFMPEG_DIR";
    private static readonly Lazy<(string? ffmpeg, string? ffprobe, string? source)> _resolved =
        new(Resolve);

    public static string FfmpegPath => _resolved.Value.ffmpeg ??
        throw new ApiException(500, "ENCODER_ERROR", "ffmpeg not found in any search path");
    public static string FfprobePath => _resolved.Value.ffprobe ??
        throw new ApiException(500, "ENCODER_ERROR", "ffprobe not found in any search path");
    public static string? Source => _resolved.Value.source;

    private static (string? ffmpeg, string? ffprobe, string? source) Resolve()
    {
        var env = Environment.GetEnvironmentVariable(FfmpegDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            var f = Path.Combine(env, "ffmpeg.exe");
            var p = Path.Combine(env, "ffprobe.exe");
            if (File.Exists(f) && File.Exists(p)) return (f, p, "env:" + FfmpegDirEnvVar);
        }

        var appDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(appDir))
        {
            var f = Path.Combine(appDir, "ffmpeg.exe");
            var p = Path.Combine(appDir, "ffprobe.exe");
            if (File.Exists(f) && File.Exists(p)) return (f, p, "app_dir");
        }

        if (!string.IsNullOrEmpty(appDir))
        {
            var cur = appDir;
            for (int i = 0; i < 8 && cur != null; i++)
            {
                var toolsBin = Path.Combine(cur, "tools", "ffmpeg", "bin");
                var f = Path.Combine(toolsBin, "ffmpeg.exe");
                var p = Path.Combine(toolsBin, "ffprobe.exe");
                if (File.Exists(f) && File.Exists(p)) return (f, p, "project_tools");
                cur = Path.GetDirectoryName(cur);
            }
        }

        string? fpath = FindInPath("ffmpeg.exe") ?? FindInPath("ffmpeg");
        string? ppath = FindInPath("ffprobe.exe") ?? FindInPath("ffprobe");
        if (fpath != null || ppath != null) return (fpath, ppath, "path");

        return (null, null, null);
    }

    private static string? FindInPath(string name)
    {
        var env = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(env)) return null;
        foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(p, name);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
