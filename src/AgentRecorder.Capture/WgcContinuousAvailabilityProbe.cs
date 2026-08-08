using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentRecorder.Capture;

public interface IWgcContinuousAvailabilityProbe
{
    WgcContinuousAvailabilityResult Check(CaptureConfig config);
}

internal interface IWgcContinuousAvailabilityWarmupProbe
{
    Task<WgcContinuousAvailabilityResult> WarmupAsync(CancellationToken cancellationToken = default);
}

public readonly record struct WgcMonitorBounds(int X, int Y, int Width, int Height);

public sealed class WgcContinuousCapabilityEvidence
{
    public WgcContinuousCapabilityEvidence(
        string helperVersion,
        string dpiAwareness,
        bool wgcSupported,
        bool d3d11Initialized,
        bool encoderCreated,
        IReadOnlyList<WgcMonitorBounds> monitors,
        bool windowCaptureSupported = false)
    {
        HelperVersion = helperVersion ?? "";
        DpiAwareness = dpiAwareness ?? "";
        WgcSupported = wgcSupported;
        D3d11Initialized = d3d11Initialized;
        EncoderCreated = encoderCreated;
        WindowCaptureSupported = windowCaptureSupported;
        Monitors = Array.AsReadOnly((monitors ?? Array.Empty<WgcMonitorBounds>()).ToArray());
    }

    public string HelperVersion { get; }
    public string DpiAwareness { get; }
    public bool WgcSupported { get; }
    public bool D3d11Initialized { get; }
    public bool EncoderCreated { get; }
    public bool WindowCaptureSupported { get; }
    public IReadOnlyList<WgcMonitorBounds> Monitors { get; }
}

public sealed class WgcContinuousAvailabilityResult
{
    public WgcContinuousAvailabilityResult(
        bool available,
        string reasonCode,
        string availabilitySource = "not_run",
        int? elapsedMs = null,
        WgcContinuousCapabilityEvidence? evidence = null)
    {
        Available = available;
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? "unknown" : reasonCode;
        AvailabilitySource = string.IsNullOrWhiteSpace(availabilitySource) ? "not_run" : availabilitySource;
        ElapsedMs = elapsedMs.HasValue ? Math.Max(0, elapsedMs.Value) : null;
        Evidence = evidence;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public string AvailabilitySource { get; }
    public int? ElapsedMs { get; }
    public WgcContinuousCapabilityEvidence? Evidence { get; }
}

/// <summary>
/// Runs the native helper's non-capturing version and capability probes.
/// Successful capability evidence is cached briefly and shared by concurrent
/// callers. Failure results are deliberately never cached.
/// </summary>
public sealed class WgcContinuousAvailabilityProbe :
    IWgcContinuousAvailabilityProbe,
    IWgcContinuousAvailabilityWarmupProbe
{
    public const string SupportedHelperVersion = "0.2.0";
    public const int VersionTimeoutMs = 1500;
    public const int ProbeTimeoutMs = 3000;
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);

    private const int MaxCacheEntries = 8;
    private static readonly TimeSpan MaxCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly Regex VersionLine = new(
        @"^\s*wgc-native-helper\s+(?<version>\d+\.\d+\.\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex MonitorLine = new(
        @"^\s*Monitor\[\d+\]:\s*x=(?<x>-?\d+)\s+y=(?<y>-?\d+)\s+width=(?<w>\d+)\s+height=(?<h>\d+)(?:\s+primary=(?:true|false))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly Func<string> _helperPathResolver;
    private readonly IWgcHelperProcessRunner _processRunner;
    private readonly Func<string, WgcHelperFileIdentity?> _helperIdentityResolver;
    private readonly Func<long> _monotonicTimestamp;
    private readonly long _timestampFrequency;
    private readonly TimeSpan _cacheTtl;
    private readonly ConcurrentDictionary<HelperIdentity, CacheEntry> _cache =
        new(HelperIdentityComparer.Instance);
    private readonly ConcurrentDictionary<HelperIdentity, Lazy<Task<RawAvailabilityResult>>> _inflight =
        new(HelperIdentityComparer.Instance);

    /// <summary>
    /// Test-only seam invoked after a caller has selected an inflight entry and
    /// before it evaluates the shared Lazy. It is intentionally inert unless a
    /// test explicitly configures it; production callers never observe it.
    /// </summary>
    internal Action? AfterInflightJoinForTests { get; set; }

    public WgcContinuousAvailabilityProbe()
        : this(WgcHelperExePathResolver.Resolve, new WgcHelperProcessRunner())
    {
    }

    public WgcContinuousAvailabilityProbe(
        Func<string> helperPathResolver,
        IWgcHelperProcessRunner processRunner)
        : this(
            helperPathResolver,
            processRunner,
            ResolveHelperIdentity,
            Stopwatch.GetTimestamp,
            Stopwatch.Frequency,
            DefaultCacheTtl)
    {
    }

    internal WgcContinuousAvailabilityProbe(
        Func<string> helperPathResolver,
        IWgcHelperProcessRunner processRunner,
        Func<string, WgcHelperFileIdentity?> helperIdentityResolver,
        Func<long> monotonicTimestamp,
        long timestampFrequency,
        TimeSpan cacheTtl)
    {
        _helperPathResolver = helperPathResolver ?? throw new ArgumentNullException(nameof(helperPathResolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _helperIdentityResolver = helperIdentityResolver ?? throw new ArgumentNullException(nameof(helperIdentityResolver));
        _monotonicTimestamp = monotonicTimestamp ?? throw new ArgumentNullException(nameof(monotonicTimestamp));
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        _timestampFrequency = timestampFrequency;
        if (cacheTtl <= TimeSpan.Zero || cacheTtl > MaxCacheTtl)
            throw new ArgumentOutOfRangeException(nameof(cacheTtl));
        _cacheTtl = cacheTtl;
    }

    public WgcContinuousAvailabilityResult Check(CaptureConfig config)
    {
        if (!IsProbeCandidate(config))
            return Unavailable("invalid_config");

        long started = _monotonicTimestamp();
        if (!TryResolveHelperIdentity(out var identity, out var helperPath, out var identityReason))
            return Unavailable(identityReason, elapsedMs: ElapsedMilliseconds(started));

        CapabilityLookup lookup;
        try
        {
            lookup = GetCapabilityAsync(identity, helperPath, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch
        {
            return Unavailable("probe_exception", "fresh_probe", ElapsedMilliseconds(started));
        }

        return MapToConfigResult(config, lookup, ElapsedMilliseconds(started));
    }

    public async Task<WgcContinuousAvailabilityResult> WarmupAsync(
        CancellationToken cancellationToken = default)
    {
        long started = _monotonicTimestamp();
        if (cancellationToken.IsCancellationRequested)
            return Unavailable("probe_cancelled", elapsedMs: ElapsedMilliseconds(started));

        if (!TryResolveHelperIdentity(out var identity, out var helperPath, out var identityReason))
            return Unavailable(identityReason, elapsedMs: ElapsedMilliseconds(started));

        CapabilityLookup lookup;
        try
        {
            lookup = await GetCapabilityAsync(identity, helperPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Unavailable("probe_cancelled", "single_flight", ElapsedMilliseconds(started));
        }
        catch
        {
            return Unavailable("probe_exception", "fresh_probe", ElapsedMilliseconds(started));
        }

        return MapRawResult(lookup, ElapsedMilliseconds(started));
    }

    private async Task<CapabilityLookup> GetCapabilityAsync(
        HelperIdentity identity,
        string helperPath,
        CancellationToken cancellationToken)
    {
        if (TryGetCached(identity, out var cachedEvidence))
        {
            return new CapabilityLookup(
                RawAvailabilityResult.Success(cachedEvidence),
                "cache_hit");
        }

        var candidate = new Lazy<Task<RawAvailabilityResult>>(
            () => Task.Run(
                () => RunFreshProbe(identity, helperPath, cancellationToken),
                CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var shared = _inflight.GetOrAdd(identity, candidate);
        bool owner = ReferenceEquals(shared, candidate);
        Task<RawAvailabilityResult>? sharedTask = null;

        try
        {
            AfterInflightJoinForTests?.Invoke();
            sharedTask = shared.Value;
            if (owner)
            {
                _ = sharedTask.ContinueWith(
                    _ => RemoveInflight(identity, shared),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            RawAvailabilityResult result = await sharedTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new CapabilityLookup(result, owner ? "fresh_probe" : "single_flight");
        }
        catch (OperationCanceledException)
        {
            return new CapabilityLookup(
                RawAvailabilityResult.Failure("probe_cancelled"),
                owner ? "fresh_probe" : "single_flight");
        }
        catch
        {
            return new CapabilityLookup(
                RawAvailabilityResult.Failure("probe_exception"),
                owner ? "fresh_probe" : "single_flight");
        }
        finally
        {
            if (owner && sharedTask?.IsCompleted == true)
                RemoveInflight(identity, shared);
        }
    }

    private RawAvailabilityResult RunFreshProbe(
        HelperIdentity identity,
        string helperPath,
        CancellationToken cancellationToken)
    {
        WgcHelperProcessResult versionResult;
        try
        {
            versionResult = _processRunner.Run(
                helperPath,
                new[] { "--version" },
                VersionTimeoutMs,
                cancellationToken);
        }
        catch
        {
            return RawAvailabilityResult.Failure("version_start_failed");
        }

        if (versionResult.TimedOut)
            return RawAvailabilityResult.Failure("version_timeout");
        if (versionResult.Cancelled)
            return RawAvailabilityResult.Failure("version_cancelled");
        if (versionResult.StandardOutputTruncated || versionResult.StandardErrorTruncated)
            return RawAvailabilityResult.Failure("version_output_invalid");
        if (versionResult.ExitCode != 0)
            return RawAvailabilityResult.Failure("version_nonzero_exit");
        if (!TryParseCompatibleVersion(versionResult.StandardOutput))
            return RawAvailabilityResult.Failure("version_incompatible");

        WgcHelperProcessResult probeResult;
        try
        {
            // --probe is intentionally the only probe mode argument. It does
            // not receive capture output, consent, begin, or stop arguments.
            probeResult = _processRunner.Run(
                helperPath,
                new[] { "--probe" },
                ProbeTimeoutMs,
                cancellationToken);
        }
        catch
        {
            return RawAvailabilityResult.Failure("probe_start_failed");
        }

        if (probeResult.TimedOut)
            return RawAvailabilityResult.Failure("probe_timeout");
        if (probeResult.Cancelled)
            return RawAvailabilityResult.Failure("probe_cancelled");
        if (probeResult.StandardOutputTruncated || probeResult.StandardErrorTruncated)
            return RawAvailabilityResult.Failure("probe_output_invalid");
        if (probeResult.ExitCode != 0)
            return RawAvailabilityResult.Failure("probe_nonzero_exit");

        try
        {
            if (!TryParseProbeOutput(probeResult.StandardOutput, out var evidence))
                return RawAvailabilityResult.Failure("probe_output_invalid");
            if (!string.Equals(evidence.DpiAwareness, "per_monitor_v2", StringComparison.OrdinalIgnoreCase))
                return RawAvailabilityResult.Failure("probe_dpi_mismatch");
            if (!evidence.WgcSupported)
                return RawAvailabilityResult.Failure("probe_wgc_unsupported");
            if (!evidence.D3d11Initialized)
                return RawAvailabilityResult.Failure("probe_d3d11_uninitialized");
            if (!evidence.EncoderCreated)
                return RawAvailabilityResult.Failure("probe_encoder_unavailable");

            _cache[identity] = new CacheEntry(evidence, _monotonicTimestamp());
            TrimCache();
            return RawAvailabilityResult.Success(evidence);
        }
        catch
        {
            return RawAvailabilityResult.Failure("probe_exception");
        }
    }

    private WgcContinuousAvailabilityResult MapToConfigResult(
        CaptureConfig config,
        CapabilityLookup lookup,
        int? elapsedMs)
    {
        if (!lookup.Result.Available || lookup.Result.Evidence == null)
            return new WgcContinuousAvailabilityResult(
                false,
                lookup.Result.ReasonCode,
                lookup.Source,
                elapsedMs);

        if (string.Equals(config.SourceKind, "window", StringComparison.Ordinal))
        {
            if (!lookup.Result.Evidence.WindowCaptureSupported)
            {
                return new WgcContinuousAvailabilityResult(
                    false,
                    "probe_window_unsupported",
                    lookup.Source,
                    elapsedMs,
                    lookup.Result.Evidence);
            }
        }
        else
        {
            var bounds = config.Bounds;
            if (!lookup.Result.Evidence.Monitors.Contains(
                    new WgcMonitorBounds(bounds.x, bounds.y, bounds.w, bounds.h)))
            {
                return new WgcContinuousAvailabilityResult(
                    false,
                    "probe_bounds_mismatch",
                    lookup.Source,
                    elapsedMs,
                    lookup.Result.Evidence);
            }
        }

        return MapRawResult(lookup, elapsedMs);
    }

    private static WgcContinuousAvailabilityResult MapRawResult(
        CapabilityLookup lookup,
        int? elapsedMs)
    {
        return new WgcContinuousAvailabilityResult(
            lookup.Result.Available,
            lookup.Result.ReasonCode,
            lookup.Source,
            elapsedMs,
            lookup.Result.Evidence);
    }

    private bool TryResolveHelperIdentity(
        out HelperIdentity identity,
        out string helperPath,
        out string reasonCode)
    {
        identity = default;
        helperPath = "";
        reasonCode = "helper_missing";

        try
        {
            helperPath = _helperPathResolver();
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch
        {
            reasonCode = "helper_resolve_failed";
            return false;
        }

        try
        {
            var fileIdentity = _helperIdentityResolver(helperPath);
            if (fileIdentity == null)
                return false;

            string fullPath = Path.GetFullPath(fileIdentity.FullPath);
            identity = new HelperIdentity(
                fullPath,
                fileIdentity.Length,
                fileIdentity.LastWriteTimeUtc.Ticks,
                SupportedHelperVersion);
            return true;
        }
        catch
        {
            reasonCode = "helper_identity_failed";
            return false;
        }
    }

    private bool TryGetCached(HelperIdentity identity, out WgcContinuousCapabilityEvidence evidence)
    {
        evidence = null!;
        if (!_cache.TryGetValue(identity, out var entry))
            return false;

        long now = _monotonicTimestamp();
        if (Elapsed(now, entry.Timestamp) >= _cacheTtl)
        {
            _cache.TryRemove(identity, out _);
            return false;
        }

        evidence = entry.Evidence;
        return true;
    }

    private void TrimCache()
    {
        while (_cache.Count > MaxCacheEntries)
        {
            var oldest = _cache
                .OrderBy(pair => pair.Value.Timestamp)
                .FirstOrDefault();
            if (oldest.Equals(default(KeyValuePair<HelperIdentity, CacheEntry>)))
                return;
            _cache.TryRemove(oldest.Key, out _);
        }
    }

    private void RemoveInflight(
        HelperIdentity identity,
        Lazy<Task<RawAvailabilityResult>> expected)
    {
        ((ICollection<KeyValuePair<HelperIdentity, Lazy<Task<RawAvailabilityResult>>>>)_inflight)
            .Remove(new KeyValuePair<HelperIdentity, Lazy<Task<RawAvailabilityResult>>>(identity, expected));
    }

    private int? ElapsedMilliseconds(long started)
    {
        long now = _monotonicTimestamp();
        TimeSpan elapsed = Elapsed(now, started);
        if (elapsed < TimeSpan.Zero)
            return 0;
        if (elapsed.TotalMilliseconds >= int.MaxValue)
            return int.MaxValue;
        return Math.Max(0, (int)elapsed.TotalMilliseconds);
    }

    private TimeSpan Elapsed(long end, long start)
    {
        if (end <= start)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((end - start) / (double)_timestampFrequency);
    }

    private static bool IsProbeCandidate(CaptureConfig? config)
    {
        if (config == null ||
            (!string.Equals(config.SourceKind, "display", StringComparison.Ordinal) &&
             !string.Equals(config.SourceKind, "window", StringComparison.Ordinal)))
            return false;
        if (config.Microphone || !config.DurationSeconds.HasValue)
            return false;
        if (config.DurationSeconds.Value is < 1 or > 10)
            return false;
        if (config.Fps is < 1 or > 60)
            return false;
        if (config.Bounds.w <= 0 || config.Bounds.h <= 0)
            return false;
        return !string.Equals(config.SourceKind, "window", StringComparison.Ordinal)
            || config.WindowHandle != nint.Zero;
    }

    private static bool TryParseCompatibleVersion(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return false;

        foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = VersionLine.Match(line);
            if (match.Success)
            {
                return string.Equals(
                    match.Groups["version"].Value,
                    SupportedHelperVersion,
                    StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static bool TryParseProbeOutput(
        string stdout,
        out WgcContinuousCapabilityEvidence evidence)
    {
        evidence = null!;
        if (string.IsNullOrWhiteSpace(stdout))
            return false;

        bool? resultOk = null;
        string? dpiAwareness = null;
        bool? wgcSupported = null;
        bool? d3d11Initialized = null;
        bool? encoderCreated = null;
        bool? windowCaptureSupported = null;
        var monitors = new List<WgcMonitorBounds>();

        foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            int separator = trimmed.IndexOf(':');
            if (separator > 0)
            {
                string key = trimmed[..separator].Trim();
                string value = trimmed[(separator + 1)..].Trim();
                if (string.Equals(key, "RESULT", StringComparison.OrdinalIgnoreCase))
                    resultOk = string.Equals(value, "OK", StringComparison.OrdinalIgnoreCase);
                else if (string.Equals(key, "DpiAwareness", StringComparison.OrdinalIgnoreCase))
                    dpiAwareness = value;
                else if (string.Equals(key, "WgcSupported", StringComparison.OrdinalIgnoreCase))
                    wgcSupported = ParseBoolean(value);
                else if (string.Equals(key, "D3d11Initialized", StringComparison.OrdinalIgnoreCase))
                    d3d11Initialized = ParseBoolean(value);
                else if (string.Equals(key, "EncoderCreated", StringComparison.OrdinalIgnoreCase))
                    encoderCreated = ParseBoolean(value);
                else if (string.Equals(key, "WindowCaptureSupported", StringComparison.OrdinalIgnoreCase))
                    windowCaptureSupported = ParseBoolean(value);
            }

            Match monitor = MonitorLine.Match(trimmed);
            if (monitor.Success
                && int.TryParse(monitor.Groups["x"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(monitor.Groups["y"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                && int.TryParse(monitor.Groups["w"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                && int.TryParse(monitor.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            {
                monitors.Add(new WgcMonitorBounds(x, y, w, h));
            }
        }

        if (resultOk != true
            || string.IsNullOrWhiteSpace(dpiAwareness)
            || !wgcSupported.HasValue
            || !d3d11Initialized.HasValue
            || !encoderCreated.HasValue
            || !windowCaptureSupported.HasValue
            || monitors.Count == 0)
            return false;

        evidence = new WgcContinuousCapabilityEvidence(
            SupportedHelperVersion,
            dpiAwareness,
            wgcSupported.Value,
            d3d11Initialized.Value,
            encoderCreated.Value,
            monitors,
            windowCaptureSupported.Value);
        return true;
    }

    private static bool ParseBoolean(string value) =>
        bool.TryParse(value, out bool result) && result;

    private static WgcContinuousAvailabilityResult Unavailable(
        string reasonCode,
        string availabilitySource = "not_run",
        int? elapsedMs = null,
        WgcContinuousCapabilityEvidence? evidence = null) =>
        new(false, reasonCode, availabilitySource, elapsedMs, evidence);

    private static WgcHelperFileIdentity? ResolveHelperIdentity(string helperPath)
    {
        string fullPath = Path.GetFullPath(helperPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return null;
        return new WgcHelperFileIdentity(fullPath, info.Length, info.LastWriteTimeUtc);
    }

    internal sealed record WgcHelperFileIdentity(
        string FullPath,
        long Length,
        DateTime LastWriteTimeUtc);

    private readonly record struct HelperIdentity(
        string FullPath,
        long Length,
        long LastWriteTimeUtcTicks,
        string SupportedVersion);

    private sealed class HelperIdentityComparer : IEqualityComparer<HelperIdentity>
    {
        public static HelperIdentityComparer Instance { get; } = new();

        public bool Equals(HelperIdentity x, HelperIdentity y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.FullPath, y.FullPath)
            && x.Length == y.Length
            && x.LastWriteTimeUtcTicks == y.LastWriteTimeUtcTicks
            && string.Equals(x.SupportedVersion, y.SupportedVersion, StringComparison.Ordinal);

        public int GetHashCode(HelperIdentity obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FullPath),
                obj.Length,
                obj.LastWriteTimeUtcTicks,
                obj.SupportedVersion);
    }

    private sealed record CacheEntry(
        WgcContinuousCapabilityEvidence Evidence,
        long Timestamp);

    private sealed class RawAvailabilityResult
    {
        private RawAvailabilityResult(
            bool available,
            string reasonCode,
            WgcContinuousCapabilityEvidence? evidence)
        {
            Available = available;
            ReasonCode = reasonCode;
            Evidence = evidence;
        }

        public bool Available { get; }
        public string ReasonCode { get; }
        public WgcContinuousCapabilityEvidence? Evidence { get; }

        public static RawAvailabilityResult Success(WgcContinuousCapabilityEvidence evidence) =>
            new(true, "available", evidence);

        public static RawAvailabilityResult Failure(string reasonCode) =>
            new(false, reasonCode, null);
    }

    private readonly record struct CapabilityLookup(
        RawAvailabilityResult Result,
        string Source);
}
