using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Logging;

/// <summary>
/// Production performance tracer implementation. Records domain events with
/// monotonic elapsed times and writes them as rolling JSONL under
/// <c>&lt;data-dir&gt;\perf\recording-traces.jsonl</c>.
/// </summary>
public sealed class RecordingPerformanceTracer : IPerformanceTracer, IBackendSelectionPerformanceTracer, IEncoderSelectionPerformanceTracer, ICapturePlanPerformanceTracer, IDisposable
{
    private readonly RollingJsonlWriter _writer;
    private readonly ConcurrentDictionary<string, long> _intentStartTicks = new();
    private readonly ConcurrentDictionary<string, TraceContext> _traceContexts = new();
    private readonly ConcurrentDictionary<string, string> _recordingToTrace = new();
    private readonly ConcurrentDictionary<string, string> _confirmationToTrace = new();
    // Bounded tombstone set that records whether a cleaned-up trace already
    // reached validation or recording terminal. Prevents late catch-chain
    // calls from recreating a context and writing duplicate events.
    private readonly ConcurrentDictionary<string, LifecycleTombstone> _lifecycleTombstones = new();
    private readonly Func<DateTime> _utcNow;
    private readonly Func<long> _timestampTicks;
    private readonly TimeSpan _terminalTtl;
    private readonly int _maxContexts;
    private long _operationCount;

    // Short per-trace lifecycle gate: protects tombstone checks, context
    // get-or-add, atomic claim of validation/terminal/first-frame flags, and
    // reverse index cleanup. For first-frame/terminal, JSON serialization and
    // queue enqueue run inside the same short critical section to guarantee
    // strict ordering; a TryEnqueue wrapper isolates any failure.
    private readonly object _lifecycleLock = new();

    public RecordingPerformanceTracer(string dataDir)
        : this(new RollingJsonlWriter(Path.Combine(dataDir, "perf", "recording-traces.jsonl")))
    {
    }

    public RecordingPerformanceTracer(RollingJsonlWriter writer)
        : this(writer, () => DateTime.UtcNow, () => Stopwatch.GetTimestamp(),
            terminalTtl: TimeSpan.FromMinutes(5), maxContexts: 10000)
    {
    }

    internal RecordingPerformanceTracer(RollingJsonlWriter writer, Func<DateTime> utcNow, Func<long> timestampTicks,
        TimeSpan? terminalTtl = null, int? maxContexts = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _timestampTicks = timestampTicks ?? throw new ArgumentNullException(nameof(timestampTicks));
        _terminalTtl = terminalTtl ?? TimeSpan.FromMinutes(5);
        _maxContexts = maxContexts ?? 10000;
    }

    public void IntentAccepted(string traceId, string endpoint, string? clientSentAtUtc = null)
    {
        _intentStartTicks[traceId] = _timestampTicks();
        var ctx = _traceContexts.GetOrAdd(traceId, _ => new TraceContext { TraceId = traceId, CreatedAt = _utcNow() });
        ctx.Endpoint = endpoint;

        var hints = ParseClientHints(clientSentAtUtc);

        Write(traceId, "intent.accepted", clientHints: hints);
    }

    public void SetEnsureContextAssociation(string traceId, EnsureContextAssociation association)
    {
        if (association == null)
            return;

        var ctx = _traceContexts.GetOrAdd(traceId, _ => new TraceContext { TraceId = traceId, CreatedAt = _utcNow() });
        ctx.EnsureAssociation = association;
    }

    public void IntentValidated(string traceId, string endpoint, bool success, string? errorCode = null)
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleTombstones.TryGetValue(traceId, out var existingTombstone) && existingTombstone.ValidationRecorded)
                return;

            var ctx = _traceContexts.GetOrAdd(traceId, _ => new TraceContext { TraceId = traceId, CreatedAt = _utcNow() });

            // Atomically claim the single validation result for this trace.
            if (Interlocked.CompareExchange(ref ctx._validationRecorded, 1, 0) != 0)
                return;

            ctx.Endpoint = endpoint;

            var tombstone = _lifecycleTombstones.GetOrAdd(traceId, _ => new LifecycleTombstone
            {
                TraceId = traceId,
                CreatedAt = _utcNow()
            });
            tombstone.ValidationRecorded = true;

            if (!success)
            {
                // A failed validation is an intent-level terminal state: there is no
                // recording and the trace context can be cleaned up promptly.
                ctx.TerminalAt = _utcNow();
            }
        }

        if (success)
        {
            Write(traceId, "intent.validated",
                data: new Dictionary<string, object?> { ["success"] = true });
        }
        else
        {
            Write(traceId, "intent.failed",
                data: new Dictionary<string, object?> { ["error_code"] = errorCode });
        }
    }

    public void CorrelationSet(string traceId, string recordingId, string? confirmationId = null, string? sourceType = null)
    {
        lock (_lifecycleLock)
        {
            var ctx = _traceContexts.GetOrAdd(traceId, _ => new TraceContext { TraceId = traceId, CreatedAt = _utcNow() });
            ctx.RecordingId = recordingId;
            if (!string.IsNullOrEmpty(confirmationId))
                ctx.ConfirmationId = confirmationId;
            if (!string.IsNullOrEmpty(sourceType))
                ctx.SourceType = sourceType;

            _recordingToTrace[recordingId] = traceId;
            if (!string.IsNullOrEmpty(confirmationId))
                _confirmationToTrace[confirmationId!] = traceId;
        }
    }

    public void ConfirmationCreated(string traceId, string recordingId, string confirmationId)
    {
        CorrelationSet(traceId, recordingId, confirmationId);
        Write(traceId, "confirmation.created", recordingId: recordingId, confirmationId: confirmationId);
    }

    public void ConfirmationShown(string traceId, string recordingId, string confirmationId)
    {
        Write(traceId, "confirmation.shown", recordingId: recordingId, confirmationId: confirmationId);
    }

    public void ConfirmationApproved(string traceId, string recordingId, string confirmationId)
    {
        Write(traceId, "confirmation.approved", recordingId: recordingId, confirmationId: confirmationId);
    }

    public void ConfirmationRejected(string traceId, string recordingId, string confirmationId)
    {
        Write(traceId, "confirmation.rejected", recordingId: recordingId, confirmationId: confirmationId);
    }

    public void ConfirmationExpired(string traceId, string recordingId, string confirmationId)
    {
        Write(traceId, "confirmation.expired", recordingId: recordingId, confirmationId: confirmationId);
    }

    public void CaptureStartRequested(string traceId, string recordingId, string backendType)
    {
        lock (_lifecycleLock)
        {
            var ctx = _traceContexts.GetOrAdd(traceId, _ => new TraceContext { TraceId = traceId, CreatedAt = _utcNow() });
            ctx.Backend = backendType;
        }

        Write(traceId, "capture.start_requested", recordingId: recordingId, backend: backendType);
    }

    public void CaptureBackendStartReturned(string traceId, string recordingId, string backendType)
    {
        Write(traceId, "capture.backend_start_returned", recordingId: recordingId, backend: backendType);
    }

    public void CaptureBackendStartFailed(string traceId, string recordingId, string backendType, string errorCode, string errorType)
    {
        Write(traceId, "capture.backend_start_failed", recordingId: recordingId, backend: backendType,
            data: new Dictionary<string, object?> { ["error_code"] = errorCode, ["error_type"] = errorType });
    }

    public void CaptureBackendSelected(
        string traceId,
        string recordingId,
        string requestedBackend,
        string selectedBackend,
        string selectionReasonCode,
        string availabilitySource,
        int? availabilityElapsedMs,
        bool fallback)
    {
        Write(traceId, "capture.backend_selected", recordingId: recordingId, backend: selectedBackend,
            data: new Dictionary<string, object?>
            {
                ["requested_backend"] = requestedBackend,
                ["selected_backend"] = selectedBackend,
                ["selection_reason_code"] = selectionReasonCode,
                ["availability_source"] = availabilitySource,
                ["availability_elapsed_ms"] = availabilityElapsedMs,
                ["fallback"] = fallback
            });
    }

    public void EncoderSelected(string traceId, string recordingId, string encoderMode, string selectionReason)
    {
        Write(traceId, "capture.encoder_selected", recordingId: recordingId,
            data: new Dictionary<string, object?>
            {
                ["encoder_mode"] = encoderMode,
                ["encoder_selection_reason"] = selectionReason
            });
    }

    public void CapturePlanCreated(string traceId, string recordingId, string requestedBackend,
        string plannedBackend, string captureSemantics, string selectionReasonCode,
        string availabilitySource, bool fallback)
    {
        Write(traceId, "capture.plan_created", recordingId: recordingId, backend: plannedBackend,
            data: new Dictionary<string, object?>
            {
                ["requested_backend"] = requestedBackend,
                ["planned_backend"] = plannedBackend,
                ["capture_semantics"] = captureSemantics,
                ["preview_semantics"] = captureSemantics,
                ["selection_reason_code"] = selectionReasonCode,
                ["availability_source"] = availabilitySource,
                ["fallback"] = fallback
            });
    }

    public void CapturePlanRevalidated(
        string traceId,
        string recordingId,
        string approvedBackend,
        string approvedSemantics,
        string approvedReasonCode,
        string revalidatedBackend,
        string revalidatedSemantics,
        string revalidatedReasonCode,
        bool semanticsChanged)
    {
        Write(traceId, "capture.plan_revalidated", recordingId: recordingId,
            backend: revalidatedBackend,
            data: new Dictionary<string, object?>
            {
                ["approved_backend"] = approvedBackend,
                ["approved_semantics"] = approvedSemantics,
                ["approved_reason_code"] = approvedReasonCode,
                ["revalidated_backend"] = revalidatedBackend,
                ["revalidated_semantics"] = revalidatedSemantics,
                ["revalidated_reason_code"] = revalidatedReasonCode,
                ["semantics_changed"] = semanticsChanged
            });
    }

    public void MicrophonePrepareStarted(string traceId, string recordingId)
    {
        Write(traceId, "microphone_prepare_started", recordingId: recordingId);
    }

    public void MicrophoneReady(string traceId, string recordingId)
    {
        Write(traceId, "microphone_ready", recordingId: recordingId);
    }

    public void CountdownStarted(string traceId, string recordingId)
    {
        Write(traceId, "countdown_started", recordingId: recordingId);
    }

    // Test-only gate: default null, production never invokes. Allows deterministic
    // concurrency tests to pause first-frame enqueue after claim and before write.
    internal Action? BeforeFirstFrameEnqueueGateForTests { get; set; }

    // Test-only gate: default null, production never invokes. Allows deterministic
    // concurrency tests to pause terminal enqueue after claim and before write.
    internal Action? BeforeTerminalEnqueueGateForTests { get; set; }

    public void CaptureFirstFrameObserved(string traceId, string recordingId, FirstFrameEvidence evidence)
    {
        if (evidence is null)
            return;

        // Tracer is the privacy and semantic trust boundary. Validate the numeric
        // evidence before claiming the exactly-once slot; invalid evidence must
        // not consume it, so a later valid observation can still be recorded.
        if (evidence.FrameNumber < 1 || evidence.TotalSizeBytes <= 0)
            return;

        long? outTimeUs = evidence.OutTimeUs.HasValue && evidence.OutTimeUs.Value >= 0
            ? evidence.OutTimeUs.Value
            : null;

        lock (_lifecycleLock)
        {
            // If the trace already reached a terminal state, do not resurrect it.
            if (_lifecycleTombstones.TryGetValue(traceId, out var tombstone) && tombstone.TerminalRecorded)
                return;

            var ctx = _traceContexts.GetOrAdd(traceId, _ => new TraceContext { TraceId = traceId, CreatedAt = _utcNow() });

            // Exactly-once per trace, even if the backend or recording engine retries.
            if (Interlocked.CompareExchange(ref ctx._firstFrameObserved, 1, 0) != 0)
                return;

            // The evidence kind is hardcoded here; never persist a backend-supplied
            // arbitrary string that could contain paths, progress text, or API keys.
            // Enqueue inside the lifecycle lock so terminal cannot claim and write
            // between first-frame claim and first-frame enqueue. Any construction,
            // serialization, writer, or test-gate failure is isolated inside
            // TryEnqueueLifecycleEvent and must not propagate to the recording flow.
            if (TryEnqueueLifecycleEvent(traceId, "capture.first_frame_observed", ctx, BeforeFirstFrameEnqueueGateForTests,
                recordingId: recordingId, backend: ctx.Backend,
                data: new Dictionary<string, object?>
                {
                    ["evidence_kind"] = "ffmpeg_progress_frame_and_output_bytes",
                    ["frame_number"] = evidence.FrameNumber,
                    ["total_size_bytes"] = evidence.TotalSizeBytes,
                    ["out_time_us"] = outTimeUs
                }))
            {
                Interlocked.Increment(ref _operationCount);
            }
        }
    }

    public void CaptureEnded(string traceId, string recordingId)
    {
        Write(traceId, "capture_ended", recordingId: recordingId);
    }

    public void FinalizationCompleted(string traceId, string recordingId, bool success)
    {
        Write(traceId, "finalization_completed", recordingId: recordingId,
            data: new Dictionary<string, object?> { ["success"] = success });
    }

    public void RecordingTerminal(string traceId, string recordingId, string status, string? stopReason = null, string? errorCode = null)
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleTombstones.TryGetValue(traceId, out var existingTombstone) && existingTombstone.TerminalRecorded)
                return;

            var ctx = _traceContexts.GetOrAdd(traceId, _ => new TraceContext { TraceId = traceId, CreatedAt = _utcNow() });

            // Atomically claim the single recording terminal event for this trace.
            if (Interlocked.CompareExchange(ref ctx._terminalRecorded, 1, 0) != 0)
                return;

            ctx.TerminalAt = _utcNow();
            ctx.RecordingId ??= recordingId;

            var tombstone = _lifecycleTombstones.GetOrAdd(traceId, _ => new LifecycleTombstone
            {
                TraceId = traceId,
                CreatedAt = _utcNow()
            });
            tombstone.TerminalRecorded = true;

            // Enqueue terminal inside the lifecycle lock so first-frame cannot
            // claim and write between terminal claim and terminal enqueue. Any
            // failure during construction, serialization, writer enqueue, or the
            // test gate is isolated and the tombstone/claim already established
            // above remains authoritative.
            if (TryEnqueueLifecycleEvent(traceId, "recording.terminal", ctx, BeforeTerminalEnqueueGateForTests,
                recordingId: recordingId,
                data: new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["stop_reason"] = stopReason,
                    ["error_code"] = errorCode
                }))
            {
                Interlocked.Increment(ref _operationCount);
            }
        }

        MaybeCleanup();
    }

    public void LongPollCompleted(string traceId, string kind, int requestedWaitMs, int actualWaitMs, bool changed, string? recordingId = null, string? confirmationId = null)
    {
        Write(traceId, "long_poll.completed", recordingId: recordingId, confirmationId: confirmationId,
            data: new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["requested_wait_ms"] = requestedWaitMs,
                ["actual_wait_ms"] = actualWaitMs,
                ["changed"] = changed
            });
    }

    /// <summary>Resolve trace id from recording or confirmation id.</summary>
    public string? ResolveTraceId(string? recordingId = null, string? confirmationId = null)
    {
        MaybeCleanup();
        if (!string.IsNullOrEmpty(recordingId) && _recordingToTrace.TryGetValue(recordingId!, out var t1))
            return t1;
        if (!string.IsNullOrEmpty(confirmationId) && _confirmationToTrace.TryGetValue(confirmationId!, out var t2))
            return t2;
        return null;
    }

    public bool HasValidationResult(string traceId)
    {
        lock (_lifecycleLock)
        {
            if (_lifecycleTombstones.TryGetValue(traceId, out var tombstone) && tombstone.ValidationRecorded)
                return true;
            return _traceContexts.TryGetValue(traceId, out var ctx) && ctx.ValidationRecorded;
        }
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();

    private static string ToSnakeCaseLower(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new System.Text.StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private void Write(string traceId, string eventName,
        string? recordingId = null, string? confirmationId = null,
        string? backend = null,
        Dictionary<string, object?>? data = null,
        Dictionary<string, object?>? clientHints = null)
    {
        try
        {
            EnqueueEvent(traceId, eventName, ctx: null, recordingId, confirmationId, backend, data, clientHints);
            Interlocked.Increment(ref _operationCount);
            MaybeCleanup();
        }
        catch
        {
            // All tracing failures must be isolated from the recording flow.
        }
    }

    private void EnqueueEvent(string traceId, string eventName, TraceContext? ctx,
        string? recordingId = null, string? confirmationId = null,
        string? backend = null,
        Dictionary<string, object?>? data = null,
        Dictionary<string, object?>? clientHints = null)
    {
        ctx ??= _traceContexts.TryGetValue(traceId, out var found) ? found : null;
        var elapsedMs = ComputeElapsedMs(traceId);
        var resolvedRecordingId = recordingId ?? ctx?.RecordingId;
        var resolvedConfirmationId = confirmationId ?? ctx?.ConfirmationId;

        var evt = new PerformanceTraceEvent
        {
            TraceId = traceId,
            Event = eventName,
            TimestampUtc = _utcNow(),
            ElapsedFromIntentMs = elapsedMs,
            RecordingId = resolvedRecordingId,
            ConfirmationId = resolvedConfirmationId,
            Endpoint = ctx?.Endpoint,
            SourceType = ctx?.SourceType,
            Backend = backend,
            ClientHints = clientHints,
            StartupKind = ctx?.EnsureAssociation?.StartupKind,
            EnsureElapsedMs = ctx?.EnsureAssociation?.EnsureElapsedMs,
            ServiceStartupElapsedMs = ctx?.EnsureAssociation?.ServiceStartupElapsedMs,
            EnsureContextStatus = ctx?.EnsureAssociation != null
                ? ToSnakeCaseLower(ctx.EnsureAssociation.Status.ToString())
                : null,
            Data = data
        };

        var line = JsonSerializer.Serialize(evt);
        _writer.Enqueue(line);
    }

    /// <summary>
    /// Enqueue a first-frame or terminal lifecycle event while isolating all
    /// failures (event construction, JSON serialization, writer enqueue, and
    /// test-only gates) from the caller. Must be called inside <see cref="_lifecycleLock"/>.
    /// </summary>
    private bool TryEnqueueLifecycleEvent(string traceId, string eventName, TraceContext ctx, Action? beforeEnqueueGate,
        string? recordingId = null, string? confirmationId = null,
        string? backend = null,
        Dictionary<string, object?>? data = null,
        Dictionary<string, object?>? clientHints = null)
    {
        try
        {
            beforeEnqueueGate?.Invoke();
            EnqueueEvent(traceId, eventName, ctx, recordingId, confirmationId, backend, data, clientHints);
            return true;
        }
        catch
        {
            // All lifecycle tracing failures must be isolated from the recording flow.
            // The caller has already claimed the exactly-once slot and/or tombstone,
            // so a retry cannot create duplicate or reordered events.
            return false;
        }
    }

    private Dictionary<string, object?>? ParseClientHints(string? clientSentAtUtc)
    {
        if (string.IsNullOrWhiteSpace(clientSentAtUtc))
            return null;

        var hints = new Dictionary<string, object?>();
        if (DateTime.TryParse(clientSentAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var clientSentAt))
        {
            var diff = (_utcNow() - clientSentAt).TotalMilliseconds;
            // Accept -60s .. +5min to tolerate minor clock skew and slow uploads.
            if (diff >= -60000.0 && diff <= 300000.0)
            {
                hints["trust"] = "untrusted_client_hint";
                hints["agent_to_server_hint_ms"] = Math.Round(diff, 2);
            }
            else
            {
                hints["trust"] = "rejected_out_of_range";
            }
        }
        else
        {
            hints["trust"] = "rejected_unparseable";
        }

        return hints;
    }

    private double ComputeElapsedMs(string traceId)
    {
        if (!_intentStartTicks.TryGetValue(traceId, out var startTicks))
            return -1.0;
        return Stopwatch.GetElapsedTime(startTicks, _timestampTicks()).TotalMilliseconds;
    }

    private void MaybeCleanup()
    {
        if (!AutoCleanupEnabled)
            return;

        var count = _traceContexts.Count;
        if (count < _maxContexts && Interlocked.Read(ref _operationCount) % 100 != 0)
            return;

        RunCleanup();
    }

    internal void RunCleanup()
    {
        var now = _utcNow();
        var terminalCutoff = now - _terminalTtl;

        // Phase 1: TTL eviction. Only terminal contexts that have aged past the
        // TTL are removed. This phase is independent of the current count.
        var expiredByTtl = _traceContexts.Values
            .Where(c => c.TerminalAt.HasValue && c.TerminalAt.Value <= terminalCutoff)
            .ToList();

        foreach (var ctx in expiredByTtl)
            RemoveContextAndUpdateTombstone(ctx, now);

        // Phase 2: capacity eviction. If still over capacity, remove exactly the
        // oldest terminal contexts required to drop to the limit. Active contexts
        // are never removed. Stable ordering: TerminalAt asc, CreatedAt asc, TraceId asc.
        if (_traceContexts.Count > _maxContexts)
        {
            var terminal = _traceContexts.Values
                .Where(c => c.TerminalAt.HasValue)
                .OrderBy(c => c.TerminalAt)
                .ThenBy(c => c.CreatedAt)
                .ThenBy(c => c.TraceId, StringComparer.Ordinal)
                .Take(_traceContexts.Count - _maxContexts)
                .ToList();

            foreach (var ctx in terminal)
                RemoveContextAndUpdateTombstone(ctx, now);
        }

        // Phase 3: tombstone maintenance. Evict aged tombstones, then apply the
        // same capacity bound as contexts.
        lock (_lifecycleLock)
        {
            foreach (var kvp in _lifecycleTombstones.ToList())
            {
                if (kvp.Value.CreatedAt <= terminalCutoff)
                    _lifecycleTombstones.TryRemove(kvp.Key, out _);
            }

            if (_lifecycleTombstones.Count > _maxContexts)
            {
                var toRemove = _lifecycleTombstones
                    .OrderBy(kvp => kvp.Value.CreatedAt)
                    .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .Take(_lifecycleTombstones.Count - _maxContexts)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in toRemove)
                    _lifecycleTombstones.TryRemove(key, out _);
            }
        }
    }

    private void RemoveContextAndUpdateTombstone(TraceContext ctx, DateTime now)
    {
        lock (_lifecycleLock)
        {
            if (!_traceContexts.TryRemove(ctx.TraceId, out var removed))
                return;

            if (!string.IsNullOrEmpty(removed.RecordingId))
                _recordingToTrace.TryRemove(removed.RecordingId, out _);
            if (!string.IsNullOrEmpty(removed.ConfirmationId))
                _confirmationToTrace.TryRemove(removed.ConfirmationId, out _);
            _intentStartTicks.TryRemove(removed.TraceId, out _);

            var tombstone = _lifecycleTombstones.GetOrAdd(removed.TraceId, _ => new LifecycleTombstone
            {
                TraceId = removed.TraceId
            });
            // Refresh the tombstone timestamp to the eviction time so TTL
            // maintenance does not immediately discard a just-cleaned trace.
            tombstone.CreatedAt = now;
            if (removed.ValidationRecorded)
                tombstone.ValidationRecorded = true;
            if (removed.TerminalRecorded)
                tombstone.TerminalRecorded = true;
        }
    }

    // Test seams --------------------------------------------------------
    internal int TraceContextCount => _traceContexts.Count;
    internal int TerminalTraceCount => _traceContexts.Values.Count(c => c.TerminalAt.HasValue);
    internal int ActiveTraceCount => _traceContexts.Values.Count(c => !c.TerminalAt.HasValue);
    internal int TombstoneCount => _lifecycleTombstones.Count;
    internal TimeSpan TerminalTtl => _terminalTtl;
    internal int MaxContexts => _maxContexts;

    /// <summary>
    /// Test seam: when false, automatic cleanup triggered by operation count
    /// or capacity is suppressed. Manual <see cref="RunCleanup"/> still runs.
    /// </summary>
    internal bool AutoCleanupEnabled { get; set; } = true;

    private sealed class LifecycleTombstone
    {
        public string TraceId { get; init; } = "";
        public bool ValidationRecorded { get; set; }
        public bool TerminalRecorded { get; set; }
        /// <summary>
        /// Marks when this tombstone became authoritative (context removed).
        /// Refreshed on eviction so validation-only tombstones are not
        /// immediately reaped by the TTL maintenance pass.
        /// </summary>
        public DateTime CreatedAt { get; set; }
    }

    private sealed class TraceContext
    {
        public string TraceId { get; init; } = "";
        public string? Endpoint { get; set; }
        public string? SourceType { get; set; }
        public string? RecordingId { get; set; }
        public string? ConfirmationId { get; set; }
        public string? Backend { get; set; }
        public DateTime? TerminalAt { get; set; }
        public DateTime CreatedAt { get; init; }
        public EnsureContextAssociation? EnsureAssociation { get; set; }

        // Atomic flags: 0 = not yet recorded, 1 = recorded.
        internal int _validationRecorded;
        internal int _terminalRecorded;
        internal int _firstFrameObserved;

        public bool ValidationRecorded => Interlocked.CompareExchange(ref _validationRecorded, 1, 1) == 1;
        public bool TerminalRecorded => Interlocked.CompareExchange(ref _terminalRecorded, 1, 1) == 1;
    }
}
