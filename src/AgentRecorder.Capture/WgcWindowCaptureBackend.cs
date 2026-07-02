using System;
using System.IO;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Capture;

/// <summary>
/// WGC (Windows Graphics Capture) window capture backend.
///
/// <para>
/// Unlike the C# projection of Windows.Graphics.Capture, this implementation
/// launches a native C++/WinRT helper process that uses
/// <c>IGraphicsCaptureItemInterop</c> to create a <c>GraphicsCaptureItem</c>
/// from an HWND, performs a single-frame capture, and writes the result to
/// a PNG file. See <c>tools/wgc-native-helper/</c>.
/// </para>
///
/// <para>
/// The helper emits stdout metadata that WgcHelperOutputParser maps to an
/// <c>OutputMeta</c>. FFmpeg-style duration / video semantics do not apply
/// to a single-frame capture, so the engine treats
/// <c>container=png / codec=still-frame</c> with a dedicated success criteria.
/// </para>
/// </summary>
public sealed class WgcWindowCaptureBackend : ICaptureBackend
{
    private IWgcHelperProcessRunner? _processRunner;
    private string? _helperExePath;
    private readonly bool _usesInjectedRunner;
    private readonly object _lock = new();
    private OutputMeta? _cachedMeta;
    private int _exitCode = -1;
    private Action<int, OutputMeta>? _onNaturalExit;

    /// <summary>Default production constructor: uses WgcHelperProcessRunner + resolves exe at Start() time.</summary>
    public WgcWindowCaptureBackend()
    {
        _usesInjectedRunner = false;
    }

    /// <summary>Test-only constructor: inject a fake process runner and a dummy exe path.</summary>
    public WgcWindowCaptureBackend(IWgcHelperProcessRunner processRunner, string helperExePath)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _helperExePath = helperExePath ?? throw new ArgumentNullException(nameof(helperExePath));
        _usesInjectedRunner = true;
    }

    public void OnNaturalExit(Action<int, OutputMeta> cb)
    {
        lock (_lock) _onNaturalExit = cb;
    }

    public int ExitCode
    {
        get { lock (_lock) return _exitCode; }
    }

    public void Start(CaptureConfig cfg)
    {
        if (cfg == null)
            throw new ArgumentNullException(nameof(cfg));

        if (!string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"WGC backend requires source_kind='window' (got '{cfg.SourceKind}'). " +
                "To use WGC, request a window source and expose an HWND via CaptureConfig.WindowHandle."
            );

        if (cfg.WindowHandle == 0)
            throw new InvalidOperationException(
                "WGC backend requires a non-zero WindowHandle (HWND). " +
                "Pass the HWND of the target window in CaptureConfig.WindowHandle."
            );

        // Lazy-init the real process runner + exe path (so the default ctor
        // can succeed without hitting the file system until actual capture time).
        if (!_usesInjectedRunner)
        {
            _processRunner = new WgcHelperProcessRunner();
            _helperExePath = WgcHelperExePathResolver.Resolve();
        }

        string pngPath = BuildPngOutputPath(cfg.OutputPath);
        var runner = new WgcHelperRunner(_helperExePath!, _processRunner!);
        var opts = new WgcHelperOptions
        {
            Hwnd = cfg.WindowHandle,
            OutputPath = pngPath,
            TimeoutMs = 10000
        };

        var result = runner.Run(opts);

        // ---- Real file size + PNG signature validation ----
        // The helper reports FileSize on stdout, but it may be 0 due to
        // parsing issues. Always check the actual output file for
        // existence and size; also verify PNG signature for still-frame.
        string resolvedOutput = !string.IsNullOrEmpty(result.OutputPath)
            ? result.OutputPath
            : pngPath;

        long actualFileSize = 0;
        bool fileExists = File.Exists(resolvedOutput);
        if (fileExists)
        {
            try { actualFileSize = new FileInfo(resolvedOutput).Length; }
            catch { actualFileSize = 0; }
        }

        // Prefer actual file size; fall back to parsed value if real size is 0
        long finalFileSize = actualFileSize > 0 ? actualFileSize : result.FileSize;

        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        bool hasValidPngSignature = false;
        if (fileExists && actualFileSize >= 8)
        {
            try
            {
                using (var fs = new FileStream(resolvedOutput, FileMode.Open, FileAccess.Read))
                {
                    byte[] header = new byte[8];
                    int read = fs.Read(header, 0, 8);
                    if (read >= 8 &&
                        header[0] == 0x89 &&
                        header[1] == 0x50 && // 'P'
                        header[2] == 0x4E && // 'N'
                        header[3] == 0x47 && // 'G'
                        header[4] == 0x0D &&
                        header[5] == 0x0A &&
                        header[6] == 0x1A &&
                        header[7] == 0x0A)
                    {
                        hasValidPngSignature = true;
                    }
                }
            }
            catch
            {
                // Leave hasValidPngSignature = false
            }
        }

        var warnings = new System.Collections.Generic.List<string>
            {
                "wgc_still_frame_only: WGC backend currently captures one PNG frame only, not MP4 video. " +
                "For a proper recording with audio/video, unset AGENT_RECORDER_WINDOW_BACKEND to use FFmpeg gdigrab."
            };

        if (!fileExists)
        {
            warnings.Add("wgc_missing_output: helper reported success but output file does not exist on disk.");
        }
        else if (actualFileSize < 512)
        {
            warnings.Add($"wgc_empty_output: output file is smaller than 512 bytes (got {actualFileSize} bytes).");
        }

        if (fileExists && !hasValidPngSignature && actualFileSize > 0)
        {
            warnings.Add("wgc_invalid_png_signature: output file exists but does not start with the standard PNG 8-byte magic header.");
        }

        var meta = new OutputMeta
        {
            SizeBytes = finalFileSize,
            DurationSeconds = 0,
            Width = result.Width,
            Height = result.Height,
            OutputPath = resolvedOutput,
            Container = "png",
            Codec = "still-frame",
            CaptureMethod = result.CaptureMethod,
            Stage = result.Stage,
            Hresult = result.Hresult,
            StderrLog = result.RawStandardError,
            OutputFileExists = fileExists,
            IsValidPngSignature = hasValidPngSignature,
            Warnings = warnings.ToArray()
        };

        lock (_lock)
        {
            _cachedMeta = meta;
            _exitCode = result.ExitCode;
        }

        if (!result.Success)
        {
            string msg = $"WGC helper failed (Stage={result.Stage}, HRESULT={result.Hresult}, Reason={result.Reason})";
            throw new InvalidOperationException(msg);
        }

        // Synchronous capture completed successfully — fire OnNaturalExit so engine can finalize.
        Action<int, OutputMeta>? cb;
        lock (_lock) cb = _onNaturalExit;
        cb?.Invoke(result.ExitCode, meta);
    }

    public OutputMeta Stop()
    {
        lock (_lock)
        {
            if (_cachedMeta != null) return _cachedMeta;
        }
        return new OutputMeta();
    }

    public void Dispose()
    {
    }

    private static string BuildPngOutputPath(string cfgOutputPath)
    {
        // Prefer project-relative .local-data/wgc-tests (helper validates the
        // path and rejects arbitrary dirs). Use a unique filename to avoid
        // collision with previous captures.
        string baseName = string.IsNullOrWhiteSpace(cfgOutputPath)
            ? "wgc-frame"
            : Path.GetFileNameWithoutExtension(cfgOutputPath);

        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture);
        string shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8);
        string fileName = $"{baseName}-wgc-{stamp}-{shortGuid}.png";

        string root;
        try
        {
            root = Directory.GetCurrentDirectory();
            string dir = Path.Combine(root, ".local-data", "wgc-tests");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
        catch
        {
            // Fall back to TEMP. Must NOT append error text to filename,
            // otherwise the path becomes invalid for the helper.
            string tempDir = Path.Combine(
                Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
                "AgentRecorder", "wgc");
            Directory.CreateDirectory(tempDir);
            return Path.Combine(tempDir, fileName);
        }
    }
}
