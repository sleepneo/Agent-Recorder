using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Logging;

/// <summary>
/// Read-only summary provider that scans the same rolling JSONL files written
/// by <see cref="RecordingPerformanceTracer"/> and returns cold/warm grouped
/// P50/P95 statistics. It is thread-safe, bounded, cached, and isolates all
/// failures so it can never break <c>/api/v1/capabilities</c>.
/// </summary>
public sealed class RollingJsonlPerformanceSummaryProvider : IPerformanceSummaryProvider
{
    // Publicly visible boundaries. These are documented in the API contract
    // via PerformanceSummary.Window and tests pin them down.
    public const int DefaultMaxTracesPerGroup = 50;
    public const int DefaultMaxFilesToRead = 4; // base + .1..3 matches RollingJsonlWriter defaults
    public const long DefaultMaxBytesPerFile = 5L * 1024 * 1024;
    public const long DefaultMaxTotalBytes = DefaultMaxBytesPerFile * DefaultMaxFilesToRead;
    public const int DefaultMaxTotalTraces = 10_000;
    public const int DefaultMaxEventLines = 100_000;
    public const int DefaultMaxLineBytes = 1024 * 1024; // 1 MiB per line (UTF-8 bytes)
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(10);

    private const int SupportedSchemaVersion = 1;
    private const int RepresentativeThreshold = 20;
    private const double MaxStageMs = 2 * 60 * 60 * 1000; // 2 hours sanity upper bound

    private readonly string _basePath;
    private readonly int _maxTracesPerGroup;
    private readonly long _maxBytesPerFile;
    private readonly long _maxTotalBytes;
    private readonly int _maxTotalTraces;
    private readonly int _maxEventLines;
    private readonly int _maxLineBytes;
    private readonly TimeSpan _cacheTtl;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, Stream> _openFile;

    private readonly object _cacheLock = new();
    private PerformanceSummary? _cachedSummary;
    private DateTime _cachedAt = DateTime.MinValue;
    private int _refreshInProgress;

    public RollingJsonlPerformanceSummaryProvider(string dataDir)
        : this(basePath: Path.Combine(dataDir, "perf", "recording-traces.jsonl"))
    {
    }

    public RollingJsonlPerformanceSummaryProvider(string basePath,
        int maxTracesPerGroup = DefaultMaxTracesPerGroup,
        long? maxBytesPerFile = null,
        long? maxTotalBytes = null,
        int? maxTotalTraces = null,
        int? maxEventLines = null,
        int? maxLineBytes = null,
        TimeSpan? cacheTtl = null,
        Func<DateTime>? utcNow = null,
        Func<string, Stream>? openFile = null)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _maxTracesPerGroup = maxTracesPerGroup > 0 ? maxTracesPerGroup : DefaultMaxTracesPerGroup;
        _maxBytesPerFile = maxBytesPerFile ?? DefaultMaxBytesPerFile;
        _maxTotalBytes = maxTotalBytes ?? DefaultMaxTotalBytes;
        _maxTotalTraces = maxTotalTraces ?? DefaultMaxTotalTraces;
        _maxEventLines = maxEventLines ?? DefaultMaxEventLines;
        _maxLineBytes = maxLineBytes ?? DefaultMaxLineBytes;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _openFile = openFile ?? OpenFileWithSharedDelete;
    }

    /// <summary>
    /// Opens the file with read sharing and delete/rename sharing. This allows
    /// <see cref="RollingJsonlWriter"/> to roll the file while the summary
    /// provider holds a read handle, without blocking the writer.
    /// </summary>
    internal static Stream OpenFileWithSharedDelete(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public PerformanceSummary GetSummary()
    {
        var now = _utcNow();
        lock (_cacheLock)
        {
            if (_cachedSummary != null && now - _cachedAt < _cacheTtl)
                return _cachedSummary;
        }

        // Only one thread performs a refresh; others fall through to the
        // (possibly stale but complete) cached snapshot.
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
        {
            lock (_cacheLock)
            {
                return _cachedSummary ?? PerformanceSummary.NoData(now, _maxTracesPerGroup);
            }
        }

        try
        {
            var refreshed = BuildSummary(now, out bool hadReadError);

            // True read/open failures should not wipe out a previously usable
            // cached summary. Return a deep-copied stale snapshot instead.
            if (hadReadError)
            {
                lock (_cacheLock)
                {
                    if (_cachedSummary != null)
                    {
                        return MakeStaleSnapshot(_cachedSummary, now);
                    }
                }
            }

            lock (_cacheLock)
            {
                _cachedSummary = refreshed;
                _cachedAt = _utcNow();
            }
            return refreshed;
        }
        catch
        {
            // Every failure path inside BuildSummary is handled; this is the
            // ultimate safety net and must never propagate. Prefer a stale
            // cached snapshot over returning a blank degraded summary.
            lock (_cacheLock)
            {
                if (_cachedSummary != null)
                {
                    return MakeStaleSnapshot(_cachedSummary, now);
                }
            }

            var degraded = PerformanceSummary.NoData(now, _maxTracesPerGroup,
                new PerformanceSummaryQuality { ReasonCode = ReasonCodes.UnexpectedProviderError });
            degraded.Status = PerformanceSummaryStatus.Degraded;
            return degraded;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
    }

    /// <summary>
    /// Creates a deep-copied degraded snapshot from a cached summary. The copy
    /// is independent so callers cannot mutate the cached DTO.
    /// </summary>
    private PerformanceSummary MakeStaleSnapshot(PerformanceSummary cached, DateTime now)
    {
        return new PerformanceSummary
        {
            SchemaVersion = cached.SchemaVersion,
            Status = PerformanceSummaryStatus.Degraded,
            GeneratedAt = now,
            Window = new PerformanceSummaryWindow
            {
                MaxTracesPerGroup = cached.Window.MaxTracesPerGroup,
                Source = cached.Window.Source
            },
            Quality = new PerformanceSummaryQuality
            {
                MalformedLineCount = cached.Quality.MalformedLineCount,
                UnsupportedSchemaCount = cached.Quality.UnsupportedSchemaCount,
                DiscardedSampleCount = cached.Quality.DiscardedSampleCount,
                UnclassifiedTraceCount = cached.Quality.UnclassifiedTraceCount,
                ReasonCode = ReasonCodes.StaleSnapshot
            },
            Groups = CloneGroups(cached.Groups)
        };
    }

    private static Dictionary<string, PerformanceSummaryGroup> CloneGroups(Dictionary<string, PerformanceSummaryGroup> source)
    {
        var clone = new Dictionary<string, PerformanceSummaryGroup>(source.Count);
        foreach (var kv in source)
        {
            var original = kv.Value;
            var groupClone = new PerformanceSummaryGroup
            {
                TraceCount = original.TraceCount,
                Quality = original.Quality,
                Metrics = new Dictionary<string, PerformanceSummaryMetric>(original.Metrics.Count)
            };
            foreach (var mkv in original.Metrics)
            {
                var m = mkv.Value;
                groupClone.Metrics[mkv.Key] = new PerformanceSummaryMetric
                {
                    SampleCount = m.SampleCount,
                    P50 = m.P50,
                    P95 = m.P95
                };
            }
            clone[kv.Key] = groupClone;
        }
        return clone;
    }

    private PerformanceSummary BuildSummary(DateTime now, out bool hadReadError)
    {
        var quality = new PerformanceSummaryQuality();
        var traces = ReadAndMergeTraces(quality, out hadReadError);

        // Classify traces into cold/warm/unclassified.
        var classified = new Dictionary<string, List<TraceAccumulator>>
        {
            [PerformanceSummaryGroups.Cold] = new(),
            [PerformanceSummaryGroups.Warm] = new()
        };

        foreach (var trace in traces)
        {
            // Validate context metrics uniformly. Both ensure_elapsed_ms and
            // service_startup_elapsed_ms are sanity-checked before deciding
            // whether the trace is usable, and every invalid value is counted.
            bool ensureValid = trace.EnsureElapsedMs.HasValue
                && IsValidContextMs(trace.EnsureElapsedMs.Value);
            bool serviceStartupValid = !trace.ServiceStartupElapsedMs.HasValue
                || IsValidContextMs(trace.ServiceStartupElapsedMs.Value);

            if (trace.EnsureElapsedMs.HasValue && !ensureValid)
                quality.DiscardedSampleCount++;
            if (trace.ServiceStartupElapsedMs.HasValue && !serviceStartupValid)
                quality.DiscardedSampleCount++;

            // Context field conflicts are order-independent data-quality loss.
            if (trace.HasContextConflict)
            {
                quality.DiscardedSampleCount++;
                quality.UnclassifiedTraceCount++;
                continue;
            }

            if ((trace.StartupKind == PerformanceSummaryGroups.Cold
                    || trace.StartupKind == PerformanceSummaryGroups.Warm)
                && ensureValid
                && trace.HasValidEnsureContext)
            {
                classified[trace.StartupKind].Add(trace);
                continue;
            }

            quality.UnclassifiedTraceCount++;
        }

        // Keep only the most recent N traces per group (by intent accepted time).
        foreach (var group in classified.Keys.ToList())
        {
            var list = classified[group];
            if (list.Count > _maxTracesPerGroup)
            {
                classified[group] = list
                    .OrderByDescending(t => t.IntentAcceptedTimestampUtc)
                    .ThenByDescending(t => t.TraceId)
                    .Take(_maxTracesPerGroup)
                    .ToList();
            }
        }

        var summary = PerformanceSummary.NoData(now, _maxTracesPerGroup, quality);
        bool hasAny = false;
        foreach (var groupName in new[] { PerformanceSummaryGroups.Cold, PerformanceSummaryGroups.Warm })
        {
            var group = BuildGroup(classified[groupName], quality);
            summary.Groups[groupName] = group;
            if (group.TraceCount > 0)
                hasAny = true;
        }

        bool hasDataLoss = quality.MalformedLineCount > 0
            || quality.UnsupportedSchemaCount > 0
            || quality.DiscardedSampleCount > 0
            || quality.UnclassifiedTraceCount > 0
            || !string.IsNullOrEmpty(quality.ReasonCode);

        if (hasAny && !hasDataLoss)
            summary.Status = PerformanceSummaryStatus.Available;
        else if (hasAny && hasDataLoss)
        {
            summary.Status = PerformanceSummaryStatus.Degraded;
            if (string.IsNullOrEmpty(quality.ReasonCode))
                quality.ReasonCode = ReasonCodes.PartialData;
        }
        else if (!hasAny && hasDataLoss)
            summary.Status = PerformanceSummaryStatus.Degraded;
        else
            summary.Status = PerformanceSummaryStatus.NoData;

        return summary;
    }

    private IReadOnlyList<TraceAccumulator> ReadAndMergeTraces(PerformanceSummaryQuality quality, out bool hadReadError)
    {
        var accumulators = new Dictionary<string, TraceAccumulator>();
        long totalBytesRead = 0;
        int totalEventLines = 0;
        bool boundaryReached = false;
        string? boundaryReason = null;
        hadReadError = false;

        foreach (var path in GetFilePaths())
        {
            if (Directory.Exists(path))
            {
                hadReadError = true;
                SetBoundary(ref boundaryReached, ref boundaryReason, ReasonCodes.ReadError);
                continue;
            }

            if (!File.Exists(path))
                continue;

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(path);
            }
            catch
            {
                hadReadError = true;
                SetBoundary(ref boundaryReached, ref boundaryReason, ReasonCodes.ReadError);
                continue;
            }

            if (fileInfo.Length > _maxBytesPerFile)
            {
                SetBoundary(ref boundaryReached, ref boundaryReason, ReasonCodes.ReadBoundaryReached);
            }

            long fileBytesRead = 0;
            Stream? stream = null;
            Utf8LineReader? reader = null;
            try
            {
                stream = _openFile(path);
                reader = new Utf8LineReader(stream, _maxLineBytes);
                while (true)
                {
                    string? line;
                    bool hasLine = reader.TryReadLine(out line, out long consumedBytes, out bool lineBoundaryExceeded);

                    // Always account for bytes consumed from the stream,
                    // even when no logical line is returned (e.g. a lone BOM).
                    fileBytesRead += consumedBytes;
                    totalBytesRead += consumedBytes;

                    if (lineBoundaryExceeded
                        || fileBytesRead > _maxBytesPerFile
                        || totalBytesRead > _maxTotalBytes)
                    {
                        SetBoundary(ref boundaryReached, ref boundaryReason, ReasonCodes.ReadBoundaryReached);
                        break;
                    }

                    if (!hasLine)
                        break;

                    totalEventLines++;
                    if (totalEventLines > _maxEventLines)
                    {
                        SetBoundary(ref boundaryReached, ref boundaryReason, ReasonCodes.ReadBoundaryReached);
                        break;
                    }

                    // Enforce the distinct-trace limit before parsing the full
                    // line, so we keep exactly the allowed range.
                    if (accumulators.Count >= _maxTotalTraces
                        && TryPeekNewTraceId(line!, accumulators, out _))
                    {
                        SetBoundary(ref boundaryReached, ref boundaryReason, ReasonCodes.ReadBoundaryReached);
                        break;
                    }

                    ProcessLine(line!, accumulators, quality);
                }
            }
            catch
            {
                hadReadError = true;
                SetBoundary(ref boundaryReached, ref boundaryReason, ReasonCodes.ReadError);
            }
            finally
            {
                reader?.Dispose();
                stream?.Dispose();
            }

            if (boundaryReached)
                break;
        }

        if (boundaryReached && string.IsNullOrEmpty(quality.ReasonCode))
            quality.ReasonCode = boundaryReason ?? ReasonCodes.ReadBoundaryReached;

        return accumulators.Values.ToList();
    }

    private static void SetBoundary(ref bool boundaryReached, ref string? boundaryReason, string reason)
    {
        boundaryReached = true;
        boundaryReason = reason;
    }

    /// <summary>
    /// UTF-8 byte-oriented bounded line reader. The per-line limit counts the
    /// line body plus its terminator (LF, CRLF, or CR); a final line without a
    /// terminator counts only body bytes. A leading UTF-8 BOM is excluded from
    /// the line limit but is counted toward the consumed-byte total. Lookahead
    /// bytes used to distinguish CR from CRLF are pushed back for the next line
    /// but are counted exactly once. Even when no logical line is returned, the
    /// bytes consumed (e.g. a lone BOM) are reported. Invalid UTF-8 is isolated
    /// by falling back to the replacement character so the line is later counted
    /// as malformed JSON rather than propagating an exception.
    /// </summary>
    private sealed class Utf8LineReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly int _maxLineBytes;
        private readonly byte[] _buffer;
        private int _bufferPos;
        private int _bufferLen;
        private bool _eof;
        private bool _bomSkipped;
        private long _totalBytesConsumed;
        private readonly Queue<byte> _pendingBytes = new();

        public Utf8LineReader(Stream stream, int maxLineBytes)
        {
            _stream = stream;
            _maxLineBytes = maxLineBytes;
            _buffer = new byte[8192];
        }

        public bool TryReadLine(out string? line, out long consumedBytes, out bool exceeded)
        {
            line = null;
            long startBytes = _totalBytesConsumed;
            consumedBytes = 0;
            exceeded = false;

            if (_eof && _pendingBytes.Count == 0)
            {
                consumedBytes = _totalBytesConsumed - startBytes;
                return false;
            }

            SkipBomOnce();

            using var content = new MemoryStream();
            int bodyBytesRead = 0;

            while (true)
            {
                byte b;
                if (_pendingBytes.Count > 0)
                {
                    b = _pendingBytes.Dequeue();
                }
                else if (!ReadByteFromStream(out b))
                {
                    _eof = true;
                    consumedBytes = _totalBytesConsumed - startBytes;
                    if (bodyBytesRead == 0)
                        return false;

                    exceeded = bodyBytesRead > _maxLineBytes;
                    line = Decode(content);
                    return true;
                }

                if (b == '\r')
                {
                    if (_pendingBytes.Count > 0)
                    {
                        b = _pendingBytes.Dequeue();
                        if (b == '\n')
                        {
                            // CRLF terminator: 2 bytes.
                            exceeded = bodyBytesRead + 2 > _maxLineBytes;
                            consumedBytes = _totalBytesConsumed - startBytes;
                            line = Decode(content);
                            return true;
                        }

                        // CR not followed by LF: line ends at CR, the byte
                        // after CR starts the following line.
                        _pendingBytes.Enqueue(b);
                        exceeded = bodyBytesRead + 1 > _maxLineBytes;
                        consumedBytes = _totalBytesConsumed - startBytes;
                        line = Decode(content);
                        return true;
                    }

                    if (!ReadByteFromStream(out byte next))
                    {
                        _eof = true;
                        exceeded = bodyBytesRead + 1 > _maxLineBytes;
                        consumedBytes = _totalBytesConsumed - startBytes;
                        line = Decode(content);
                        return true;
                    }

                    if (next == '\n')
                    {
                        exceeded = bodyBytesRead + 2 > _maxLineBytes;
                        consumedBytes = _totalBytesConsumed - startBytes;
                        line = Decode(content);
                        return true;
                    }

                    // CR not followed by LF: line ends at CR, next byte starts
                    // the following line.
                    _pendingBytes.Enqueue(next);
                    exceeded = bodyBytesRead + 1 > _maxLineBytes;
                    consumedBytes = _totalBytesConsumed - startBytes;
                    line = Decode(content);
                    return true;
                }

                if (b == '\n')
                {
                    exceeded = bodyBytesRead + 1 > _maxLineBytes;
                    consumedBytes = _totalBytesConsumed - startBytes;
                    line = Decode(content);
                    return true;
                }

                bodyBytesRead++;
                if (bodyBytesRead <= _maxLineBytes)
                {
                    content.WriteByte(b);
                }
                else if (!exceeded)
                {
                    exceeded = true;
                }
            }
        }

        private bool ReadByteFromStream(out byte b)
        {
            if (_bufferPos < _bufferLen)
            {
                b = _buffer[_bufferPos++];
                _totalBytesConsumed++;
                return true;
            }

            _bufferLen = _stream.Read(_buffer, 0, _buffer.Length);
            _bufferPos = 0;
            if (_bufferLen == 0)
            {
                b = 0;
                return false;
            }

            b = _buffer[_bufferPos++];
            _totalBytesConsumed++;
            return true;
        }

        private void SkipBomOnce()
        {
            if (_bomSkipped) return;
            _bomSkipped = true;

            // Try to read and discard UTF-8 BOM. If the stream does not start
            // with BOM, push the bytes back for normal line reading.
            byte b1 = 0, b2 = 0, b3 = 0;
            bool has1 = ReadByteFromStream(out b1);
            bool has2 = has1 && ReadByteFromStream(out b2);
            bool has3 = has2 && ReadByteFromStream(out b3);

            if (has1 && has2 && has3 && b1 == 0xEF && b2 == 0xBB && b3 == 0xBF)
            {
                return;
            }

            // Push back so the first pending byte dequeued is b1.
            if (has1) _pendingBytes.Enqueue(b1);
            if (has2) _pendingBytes.Enqueue(b2);
            if (has3) _pendingBytes.Enqueue(b3);
        }

        private static string Decode(MemoryStream ms)
        {
            var bytes = ms.ToArray();
            // Invalid UTF-8 sequences become U+FFFD so they are later counted
            // as malformed JSON instead of throwing here.
            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }

    private void ProcessLine(string line, Dictionary<string, TraceAccumulator> accumulators, PerformanceSummaryQuality quality)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schema_version", out var schemaProp)
                || schemaProp.ValueKind != JsonValueKind.Number
                || schemaProp.GetInt32() != SupportedSchemaVersion)
            {
                quality.UnsupportedSchemaCount++;
                return;
            }

            if (!root.TryGetProperty("trace_id", out var traceIdProp)
                || traceIdProp.ValueKind != JsonValueKind.String)
            {
                quality.MalformedLineCount++;
                return;
            }

            var traceId = traceIdProp.GetString()!;
            if (!accumulators.TryGetValue(traceId, out var acc))
            {
                acc = new TraceAccumulator { TraceId = traceId };
                accumulators[traceId] = acc;
            }

            if (!root.TryGetProperty("event", out var eventProp)
                || eventProp.ValueKind != JsonValueKind.String)
            {
                return; // unknown/empty event: ignore
            }

            var eventName = eventProp.GetString()!;
            var timestamp = ReadTimestamp(root);
            var hasElapsed = TryReadElapsedMs(root, out var elapsedMs);

            // Out-of-range event timestamps are data-quality loss even if the
            // event is not ultimately used in a metric.
            if (hasElapsed && !IsValidLatency(elapsedMs))
                quality.DiscardedSampleCount++;

            acc.ObserveEvent(eventName, timestamp, elapsedMs);
            acc.ObserveContext(root);
        }
        catch (JsonException)
        {
            quality.MalformedLineCount++;
        }
        catch
        {
            quality.MalformedLineCount++;
        }
    }

    private static DateTime ReadTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp_utc", out var prop)
            && prop.ValueKind == JsonValueKind.String
            && DateTime.TryParse(prop.GetString(), out var dt))
        {
            return dt.ToUniversalTime();
        }
        return DateTime.MinValue;
    }

    private static bool TryReadElapsedMs(JsonElement root, out double elapsedMs)
    {
        if (root.TryGetProperty("elapsed_from_intent_ms", out var prop)
            && prop.ValueKind == JsonValueKind.Number)
        {
            elapsedMs = prop.GetDouble();
            return true;
        }
        elapsedMs = double.NaN;
        return false;
    }

    /// <summary>
    /// Lightweight trace_id peek used to enforce the distinct-trace boundary
    /// before paying the cost of a full parse and accumulator mutation.
    /// </summary>
    private static bool TryPeekNewTraceId(string line, Dictionary<string, TraceAccumulator> accumulators, out string? traceId)
    {
        traceId = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("trace_id", out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                traceId = prop.GetString();
                if (!string.IsNullOrEmpty(traceId) && !accumulators.ContainsKey(traceId))
                    return true;
            }
        }
        catch
        {
            // Malformed lines will be counted by ProcessLine.
        }
        return false;
    }

    private PerformanceSummaryGroup BuildGroup(IReadOnlyList<TraceAccumulator> traces, PerformanceSummaryQuality quality)
    {
        var group = new PerformanceSummaryGroup
        {
            TraceCount = traces.Count,
            Quality = traces.Count >= RepresentativeThreshold
                ? PerformanceSummaryQualityLabels.Representative
                : PerformanceSummaryQualityLabels.Preliminary
        };

        if (traces.Count == 0)
            return group;

        var ensure = CollectContextMs(traces, t => t.EnsureElapsedMs);
        var serviceStartup = CollectContextMs(traces, t => t.ServiceStartupElapsedMs);
        var requestToShown = CollectDifference(traces, "intent.accepted", "confirmation.shown", quality);
        var shownToApproved = CollectDifference(traces, "confirmation.shown", "confirmation.approved", quality);
        var approvedToFirstFrame = CollectDifference(traces, "confirmation.approved", "capture.first_frame_observed", quality);
        var requestToFirstFrame = CollectDifference(traces, "intent.accepted", "capture.first_frame_observed", quality);

        AddMetric(group, "ensure_running_ms", ensure);
        AddMetric(group, "service_startup_ms", serviceStartup);
        AddMetric(group, "request_to_confirmation_shown_ms", requestToShown);
        AddMetric(group, "confirmation_shown_to_approved_ms", shownToApproved);
        AddMetric(group, "approved_to_first_frame_progress_ms", approvedToFirstFrame);
        AddMetric(group, "request_to_first_frame_progress_ms", requestToFirstFrame);

        return group;
    }

    private static List<double> CollectContextMs(IReadOnlyList<TraceAccumulator> traces,
        Func<TraceAccumulator, long?> selector)
    {
        var result = new List<double>(traces.Count);
        foreach (var t in traces)
        {
            if (selector(t) is { } v && IsValidContextMs(v))
                result.Add(v);
        }
        return result;
    }

    private static List<double> CollectDifference(IReadOnlyList<TraceAccumulator> traces,
        string startEvent, string endEvent, PerformanceSummaryQuality quality)
    {
        var result = new List<double>(traces.Count);
        foreach (var t in traces)
        {
            if (!t.TryGetElapsed(startEvent, out var start)
                || !t.TryGetElapsed(endEvent, out var end))
                continue;

            var diff = end - start;
            if (!IsValidLatency(diff))
            {
                quality.DiscardedSampleCount++;
                continue;
            }

            result.Add(diff);
        }
        return result;
    }

    private static void AddMetric(PerformanceSummaryGroup group, string name, List<double> samples)
    {
        if (samples.Count == 0)
            return;

        samples.Sort();
        group.Metrics[name] = new PerformanceSummaryMetric
        {
            SampleCount = samples.Count,
            P50 = RoundPercentile(PercentileNearestRank(samples, 50)),
            P95 = RoundPercentile(PercentileNearestRank(samples, 95))
        };
    }

    /// <summary>
    /// Nearest-rank percentile. Rank = ceil(P/100 * N), 1-indexed.
    /// Documented in API docs; deterministic and independent of interpolation.
    /// </summary>
    private static double PercentileNearestRank(List<double> sorted, int p)
    {
        int n = sorted.Count;
        if (n == 0) return double.NaN;
        if (n == 1) return sorted[0];

        var rank = (int)Math.Ceiling(p / 100.0 * n);
        if (rank < 1) rank = 1;
        if (rank > n) rank = n;
        return sorted[rank - 1];
    }

    private static double RoundPercentile(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return value;
        return Math.Round(value, 1, MidpointRounding.AwayFromZero);
    }

    private static bool IsValidLatency(double latency)
    {
        if (double.IsNaN(latency) || double.IsInfinity(latency))
            return false;
        if (latency < 0)
            return false;
        if (latency > MaxStageMs)
            return false;
        return true;
    }

    private static bool IsValidContextMs(long value)
    {
        if (value < 0)
            return false;
        if (value > MaxStageMs)
            return false;
        return true;
    }

    private IReadOnlyList<string> GetFilePaths()
    {
        var result = new List<string> { _basePath };
        var ext = Path.GetExtension(_basePath);
        var stem = _basePath.Substring(0, _basePath.Length - ext.Length);
        for (int i = 1; i < DefaultMaxFilesToRead; i++)
        {
            result.Add($"{stem}.{i}{ext}");
        }
        return result;
    }

    private static class ReasonCodes
    {
        public const string ReadBoundaryReached = "read_boundary_reached";
        public const string ReadError = "read_error";
        public const string PartialData = "partial_data";
        public const string UnexpectedProviderError = "unexpected_provider_error";
        public const string StaleSnapshot = "stale_snapshot";
    }

    private sealed class TraceAccumulator
    {
        public string TraceId { get; init; } = "";
        public string? StartupKind => _startupKind;
        public long? EnsureElapsedMs => _ensureElapsedMs;
        public long? ServiceStartupElapsedMs => _serviceStartupElapsedMs;
        public string? EnsureContextStatus => _ensureContextStatus;
        public bool HasValidEnsureContext { get; private set; }
        public bool HasContextConflict { get; private set; }
        public DateTime IntentAcceptedTimestampUtc { get; private set; } = DateTime.MinValue;

        private string? _startupKind;
        private long? _ensureElapsedMs;
        private long? _serviceStartupElapsedMs;
        private string? _ensureContextStatus;

        private readonly Dictionary<string, (DateTime TimestampUtc, double ElapsedMs)> _events = new();

        public void ObserveEvent(string eventName, DateTime timestampUtc, double elapsedMs)
        {
            if (!_events.TryGetValue(eventName, out var existing))
            {
                if (IsValidLatency(elapsedMs))
                {
                    _events[eventName] = (timestampUtc, elapsedMs);
                    if (eventName == "intent.accepted")
                        IntentAcceptedTimestampUtc = timestampUtc;
                }
                return;
            }

            // Deterministic: keep the earliest legal candidate regardless of
            // file enumeration order. If the new value is earlier and legal,
            // replace; if the existing value is illegal and the new value is
            // legal, replace.
            bool existingValid = IsValidLatency(existing.ElapsedMs);
            bool newValid = IsValidLatency(elapsedMs);

            if (!existingValid && newValid)
            {
                _events[eventName] = (timestampUtc, elapsedMs);
                if (eventName == "intent.accepted")
                    IntentAcceptedTimestampUtc = timestampUtc;
            }
            else if (existingValid && newValid)
            {
                // Deterministic tie-breaker: earliest elapsed wins; if equal,
                // keep the candidate with the earlier timestamp. This makes the
                // choice independent of file enumeration order.
                if (elapsedMs < existing.ElapsedMs
                    || (elapsedMs == existing.ElapsedMs && timestampUtc < existing.TimestampUtc))
                {
                    _events[eventName] = (timestampUtc, elapsedMs);
                    if (eventName == "intent.accepted")
                        IntentAcceptedTimestampUtc = timestampUtc;
                }
            }
        }

        public void ObserveContext(JsonElement root)
        {
            ObserveString(root, "startup_kind", ref _startupKind);
            ObserveLong(root, "ensure_elapsed_ms", ref _ensureElapsedMs);
            ObserveLong(root, "service_startup_elapsed_ms", ref _serviceStartupElapsedMs);
            ObserveString(root, "ensure_context_status", ref _ensureContextStatus, onFirstSet: v =>
            {
                HasValidEnsureContext = v == "consumed";
            });
        }

        private void ObserveString(JsonElement root, string propertyName, ref string? field, Action<string>? onFirstSet = null)
        {
            if (!root.TryGetProperty(propertyName, out var prop)
                || prop.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var value = prop.GetString();
            if (string.IsNullOrEmpty(value))
                return;

            if (field == null)
            {
                field = value;
                onFirstSet?.Invoke(value);
                return;
            }

            if (field != value)
            {
                HasContextConflict = true;
            }
        }

        private void ObserveLong(JsonElement root, string propertyName, ref long? field)
        {
            if (!root.TryGetProperty(propertyName, out var prop)
                || prop.ValueKind != JsonValueKind.Number)
            {
                return;
            }

            var value = prop.GetInt64();

            if (field == null)
            {
                field = value;
                return;
            }

            if (field != value)
            {
                HasContextConflict = true;
            }
        }

        public bool TryGetElapsed(string eventName, out double elapsedMs)
        {
            if (_events.TryGetValue(eventName, out var e) && !double.IsNaN(e.ElapsedMs))
            {
                elapsedMs = e.ElapsedMs;
                return true;
            }
            elapsedMs = double.NaN;
            return false;
        }
    }
}
