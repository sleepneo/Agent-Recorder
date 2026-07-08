using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Security;
using AgentRecorder.Windows;
using ApiException = AgentRecorder.Infrastructure.ApiException;
namespace AgentRecorder.Core;

public sealed class RecordingEngine
{
    internal readonly ConcurrentDictionary<string, Recording> _recs = new();
    internal readonly ConcurrentDictionary<string, Confirmation> _confs = new();
    private readonly AuditLogger _audit;
    private readonly object _lock = new();
    private ITrayContext? _tray;

    // State change notification: incremented on every recording/confirmation state transition,
    // used by GetConfirmationWait/GetStatusWait to detect changes via Monitor.Wait/PulseAll.
    internal int _stateVersion = 0;

    /// <summary>
    /// Factory used to select an ICaptureBackend for a given source type.
    /// Default: <c>CaptureBackendSelector.Select(sourceType)</c>.
    /// Replaceable for tests (e.g. to inject a WgcWindowCaptureBackend
    /// wired to a fake process runner).
    /// </summary>
    public Func<string, (ICaptureBackend Backend, string BackendType)> BackendFactory { get; set; }
        = CaptureBackendSelector.Select;

    public RecordingEngine(AuditLogger audit) => _audit = audit;
    public void SetTray(ITrayContext tray) => _tray = tray;

    /// <summary>
    /// Bumps _stateVersion and pulses all waiters on _lock.
    /// Called after every recording/confirmation state transition.
    /// </summary>
    internal void BumpStateVersion()
    {
        lock (_lock)
        {
            _stateVersion++;
            Monitor.PulseAll(_lock);
        }
    }

    public object CreateRecording(JsonNode cfg, string agent, ITrayContext tray)
    {
        // =====================================================================
        // Phase 1: Extract nested metadata (outside lock, no expensive work)
        // =====================================================================
        string? nestedRole = cfg["nested"]?["role"]?.GetValue<string>();
        string? parentId = cfg["nested"]?["parent_recording_id"]?.GetValue<string>();
        string? sessionId = cfg["nested"]?["session_id"]?.GetValue<string>();

        bool isNested = nestedRole == "outer" || nestedRole == "inner";

        // =====================================================================
        // Phase 2: Pre-flight concurrency + nested role/parent gate
        //         (coarse check, before expensive Build)
        // =====================================================================
        lock (_lock)
        {
            var active = _recs.Values
                .Where(r => r.State is RecState.recording or RecState.stopping or RecState.pending_confirmation)
                .ToList();

            if (isNested)
            {
                // For explicit nested requests, prioritize role-specific errors over
                // the generic count error. This gives users actionable error messages.
                if (nestedRole == "outer")
                {
                    if (active.Any(r => r.NestedRole == "outer"))
                        throw new ApiException(409, "OUTER_RECORDING_ALREADY_EXISTS",
                            "A nested outer recording already exists. Only one outer recording is allowed.");
                }
                else if (nestedRole == "inner")
                {
                    if (string.IsNullOrEmpty(parentId))
                        throw new ApiException(400, "INVALID_ARGUMENT",
                            "nested.role=inner requires parent_recording_id");
                    if (!_recs.TryGetValue(parentId!, out var parent))
                        throw new ApiException(404, "PARENT_RECORDING_NOT_FOUND",
                            $"Parent recording '{parentId}' not found.");
                    // Strict parent state requirement: parent must be ACTIVELY RECORDING,
                    // not pending_confirmation, not stopping, not completed, etc.
                    // This prevents the "ghost parent" anti-pattern where an inner is
                    // created before the outer's confirmation flow is complete.
                    if (parent.State != RecState.recording)
                        throw new ApiException(409, "PARENT_NOT_RECORDING",
                            $"Parent recording '{parentId}' is not in 'recording' state (current state={parent.State}). " +
                            "Inner recording can only be created when the parent outer is actively recording.");
                    if (parent.NestedRole != "outer")
                        throw new ApiException(400, "PARENT_NOT_OUTER",
                            $"Parent recording '{parentId}' does not have nested.role='outer'.");
                    if (active.Any(r => r.NestedRole == "inner"))
                        throw new ApiException(409, "INNER_RECORDING_ALREADY_EXISTS",
                            "A nested inner recording already exists. Only one inner recording is allowed.");
                    if (!string.IsNullOrEmpty(sessionId) &&
                        !string.IsNullOrEmpty(parent.NestedSessionId) &&
                        sessionId != parent.NestedSessionId)
                        throw new ApiException(400, "SESSION_ID_MISMATCH",
                            "nested.session_id does not match parent's session_id.");
                }

                // Count check only reached if role-specific checks passed
                if (active.Count >= 2)
                    throw new ApiException(409, "TOO_MANY_CONCURRENT_RECORDINGS",
                        "Nested recording MVP supports at most 2 concurrent recordings (1 outer + 1 inner).");
            }
            else
            {
                if (active.Count >= 1)
                    throw new ApiException(409, "RECORDING_ALREADY_RUNNING",
                        "Another recording is already running. Stop it before starting a new one. " +
                        "To use nested recording, specify nested.role=outer/inner in the request body.");
            }
        }

        // =====================================================================
        // Phase 3: Build recording config (may enumerate displays/windows,
        // validate nested.role in ConfigParser Step 0, etc. This is expensive
        // so it runs outside lock.)
        // =====================================================================
        var rec = ConfigParser.Build(cfg, agent, out var summary);

        // =====================================================================
        // Phase 4: Final guard + atomic register (prevents race condition where
        // two requests pass Phase-2 check, then both register.)
        // IMPORTANT: This Phase-4 must re-execute the COMPLETE guard logic,
        // not just the count check. During Phase-3 (Build), another request
        // may have registered an outer/inner that changes the guard outcome.
        // =====================================================================
        lock (_lock)
        {
            var currentActive = _recs.Values
                .Where(r => r.State is RecState.recording or RecState.stopping or RecState.pending_confirmation)
                .ToList();

            if (isNested)
            {
                // For explicit nested requests, prioritize role-specific errors over
                // the generic count error. This gives users actionable error messages:
                // "you already have an outer/inner" is more useful than "too many recordings".
                if (nestedRole == "outer")
                {
                    // Re-check 1: no other active outer (race condition defense)
                    if (currentActive.Any(r => r.NestedRole == "outer"))
                        throw new ApiException(409, "OUTER_RECORDING_ALREADY_EXISTS",
                            "A nested outer recording already exists. Only one outer recording is allowed.");
                }
                else if (nestedRole == "inner")
                {
                    // Re-check 2: parent must still exist and be valid
                    if (string.IsNullOrEmpty(parentId))
                        throw new ApiException(400, "INVALID_ARGUMENT",
                            "nested.role=inner requires parent_recording_id");
                    if (!_recs.TryGetValue(parentId!, out var parent))
                        throw new ApiException(404, "PARENT_RECORDING_NOT_FOUND",
                            $"Parent recording '{parentId}' not found.");
                    // Re-check 3: parent must still be recording (may have transitioned during Build)
                    if (parent.State != RecState.recording)
                        throw new ApiException(409, "PARENT_NOT_RECORDING",
                            $"Parent recording '{parentId}' is no longer in 'recording' state (current state={parent.State}).");
                    // Re-check 4: parent must still be outer
                    if (parent.NestedRole != "outer")
                        throw new ApiException(400, "PARENT_NOT_OUTER",
                            $"Parent recording '{parentId}' does not have nested.role='outer'.");
                    // Re-check 5: no other active inner (race condition defense)
                    if (currentActive.Any(r => r.NestedRole == "inner"))
                        throw new ApiException(409, "INNER_RECORDING_ALREADY_EXISTS",
                            "A nested inner recording already exists. Only one inner recording is allowed.");
                    // Re-check 6: session_id must still match
                    if (!string.IsNullOrEmpty(sessionId) &&
                        !string.IsNullOrEmpty(parent.NestedSessionId) &&
                        sessionId != parent.NestedSessionId)
                        throw new ApiException(400, "SESSION_ID_MISMATCH",
                            "nested.session_id does not match parent's session_id.");
                }

                // Re-check 7: concurrent count (only reached if role-specific checks passed)
                if (currentActive.Count >= 2)
                    throw new ApiException(409, "TOO_MANY_CONCURRENT_RECORDINGS",
                        "Nested recording MVP supports at most 2 concurrent recordings (1 outer + 1 inner).");
            }
            else
            {
                // Re-check: non-nested still enforces single recording
                if (currentActive.Count >= 1)
                    throw new ApiException(409, "RECORDING_ALREADY_RUNNING",
                        "Another recording is already running. Stop it before starting a new one. " +
                        "To use nested recording, specify nested.role=outer/inner in the request body.");
            }

            _recs[rec.Id] = rec;
        }

        _audit.Log("recording.requested", new
        {
            recording_id = rec.Id,
            agent, source_type = rec.SourceType,
            audio_microphone = rec.Microphone, requires_confirmation = true,
            nested_role = rec.NestedRole ?? "none",
            parent_recording_id = rec.ParentRecordingId ?? ""
        });

        bool needConfirm = PolicyEngine.RequiresConfirmation();

        if (needConfirm)
        {
            var conf = new Confirmation { RecordingId = rec.Id };
            rec.ConfirmationId = conf.Id;
            rec.State = RecState.pending_confirmation;
            _confs[conf.Id] = conf;
            BumpStateVersion();
            _audit.Log("confirmation.created", new { recording_id = rec.Id, confirmation_id = conf.Id, nested_role = rec.NestedRole ?? "none" });

            tray.RequestConfirmation(summary, approved =>
            {
                if (conf.Status != "pending") return;
                if (approved)
                {
                    conf.Status = "approved";
                    BumpStateVersion();
                    _audit.Log("confirmation.approved", new { recording_id = rec.Id, confirmation_id = conf.Id });
                    StartCapture(rec, tray);
                }
                else
                {
                    conf.Status = "rejected";
                    rec.State = RecState.rejected;
                    BumpStateVersion();
                    _audit.Log("confirmation.rejected", new { recording_id = rec.Id, confirmation_id = conf.Id });
                    TrySetIdleOnAllDone(tray);
                }
            });

            Task.Delay(TimeSpan.FromSeconds(conf.TimeoutSeconds)).ContinueWith(_ =>
            {
                if (conf.Status != "pending") return;
                conf.Status = "expired";
                if (rec.State == RecState.pending_confirmation) rec.State = RecState.expired;
                BumpStateVersion();
                _audit.Log("confirmation.expired", new { recording_id = rec.Id, confirmation_id = conf.Id });
                TrySetIdleOnAllDone(tray);
            });

            return new
            {
                status = "requires_user_confirmation",
                confirmation_id = conf.Id,
                summary
            };
        }

        StartCapture(rec, tray);
        return new
        {
            recording_id = rec.Id, status = "recording",
            started_at = Iso(rec.StartedAtUtc), expected_output = rec.OutputPath
        };
    }

    /// <summary>
    /// Test-only helper: directly call StartCapture with a Recording that
    /// has already been populated (SourceType, Config, Backend, etc.).
    /// Bypasses CreateRecording and its window / display enum lookups.
    /// </summary>
    public void StartCaptureForTests(Recording rec, ITrayContext tray)
    {
        if (rec == null) throw new ArgumentNullException(nameof(rec));
        // Mimic what CreateRecording does: register by id so GetStatus /
        // GetOutput / List can find it.
        _recs[rec.Id] = rec;
        StartCapture(rec, tray);
    }

    private void StartCapture(Recording rec, ITrayContext tray)
    {
        // Select backend FIRST, so WGC still-frame backends can signal
        // "I am synchronous and might complete during Start()".
        var selection = BackendFactory(rec.SourceType);
        rec.Backend = selection.Backend;
        rec.BackendType = selection.BackendType;

        _audit.Log("recording.backend_selected", new
        {
            recording_id = rec.Id,
            source_type = rec.SourceType,
            backend = rec.BackendType
        });

        // Hook natural exit BEFORE setting state and BEFORE calling
        // Backend.Start(). This way a synchronous backend (like WGC
        // still-frame) can FinalizeRecording() from inside Start(),
        // which will bump state recording -> completed/failed.
        rec.Backend.OnNaturalExit((exitCode, meta) =>
        {
            FinalizeRecording(rec, meta, exitCode, natural: true, tray);
        });

        // Set state to recording NOW, before Backend.Start() runs.
        // If Backend.Start() runs synchronously and completes (WGC),
        // FinalizeRecording will bump state recording -> completed/failed
        // without being overwritten afterwards.
        rec.State = RecState.recording;
        rec.StartedAtUtc = DateTime.UtcNow;
        BumpStateVersion();
        tray.SetRecording(rec);

        // Start the backend FIRST to populate CommandArgs,
        // THEN record audit with the actual ffmpeg_args.
        try
        {
            rec.Backend.Start(rec.Config);

            _audit.Log("recording.started", new
            {
                recording_id = rec.Id,
                output_path = rec.OutputPath,
                backend = rec.BackendType,
                ffmpeg_args = rec.Config.CommandArgs ?? ""
            });
        }
        catch (Exception ex)
        {
            // Only overwrite the state if Backend.Start() failed to
            // finalize itself. A backend that already called FinalizeRecording
            // leaves state as completed/failed, which we must NOT undo.
            if (rec.State == RecState.recording)
            {
                rec.State = RecState.failed;
                BumpStateVersion();
            }
            rec.Error = ex.Message;
            rec.Warnings.Add("launch_error: " + ex.Message);
            _audit.Log("recording.failed", new
            {
                recording_id = rec.Id,
                backend = rec.BackendType,
                error = ex.Message
            });
            tray.SetIdle(rec);
            tray.ShowError("Recording failed: " + ex.Message);
        }
    }

    private void FinalizeRecording(Recording rec, OutputMeta meta, int exitCode, bool natural, ITrayContext tray)
    {
        rec.CompletedAtUtc = DateTime.UtcNow;
        rec.ExitCode = exitCode;
        rec.LastMeta = meta;

        if (!string.IsNullOrEmpty(meta.StderrLog))
        {
            int start = Math.Max(0, meta.StderrLog.Length - 1000);
            rec.StderrExcerpt = meta.StderrLog.Substring(start);
        }

        var expected = rec.DurationSeconds ?? 0;
        long minSize = 512;
        bool fileOk = meta.SizeBytes > minSize;
        bool durationOk = meta.DurationSeconds > 0;
        bool rangeOk = expected == 0 || (meta.DurationSeconds >= expected * 0.3 && meta.DurationSeconds <= expected * 1.5);
        bool exitOk = exitCode == 0;

        bool isWgcStillFrame = string.Equals(meta.Container, "png", StringComparison.Ordinal) &&
                               string.Equals(meta.Codec, "still-frame", StringComparison.Ordinal);

        bool success;
        if (isWgcStillFrame && string.Equals(rec.BackendType, "wgc", StringComparison.OrdinalIgnoreCase))
        {
            // WGC still-frame: require valid PNG signature on disk in addition
            // to exit==0, reasonable size, width/height > 0. This replaces the
            // previous "warning-only" check so invalid-PNG captures end in
            // state=failed instead of state=completed.
            success = exitOk
                && meta.OutputFileExists
                && fileOk
                && meta.Width > 0
                && meta.Height > 0
                && meta.IsValidPngSignature;
            if (!success)
            {
                if (!exitOk) rec.Warnings.Add($"wgc_non_zero_exit: helper exit_code={exitCode}");
                if (!meta.OutputFileExists) rec.Warnings.Add("wgc_missing_output: helper reported success but output file is absent on disk");
                if (!fileOk) rec.Warnings.Add($"wgc_empty_output: file size {meta.SizeBytes} bytes < {minSize}");
                if (meta.Width == 0 || meta.Height == 0)
                    rec.Warnings.Add($"wgc_zero_dimensions: width={meta.Width} height={meta.Height}");
                if (meta.OutputFileExists && !meta.IsValidPngSignature)
                    rec.Warnings.Add("wgc_invalid_png_signature: output file exists but does not start with the standard PNG 8-byte magic header");
            }
        }
        else
        {
            success = fileOk && durationOk && exitOk && rangeOk;
            if (!success)
            {
                if (!fileOk) rec.Warnings.Add($"empty_output: file size {meta.SizeBytes} bytes < {minSize}");
                if (!durationOk) rec.Warnings.Add($"zero_duration: ffprobe returned duration=0");
                if (!rangeOk && expected > 0) rec.Warnings.Add($"duration_out_of_range: expected ~{expected}s got {meta.DurationSeconds:F1}s");
                if (!exitOk) rec.Warnings.Add($"non_zero_exit: ffmpeg exit_code={exitCode}");
            }
        }

        if (success)
        {
            rec.State = RecState.completed;
            BumpStateVersion();
            _audit.Log("recording.completed", new
            {
                recording_id = rec.Id,
                backend = rec.BackendType,
                duration_seconds = meta.DurationSeconds,
                size_bytes = meta.SizeBytes,
                container = meta.Container ?? "mp4",
                codec = meta.Codec ?? "h264",
                capture_method = meta.CaptureMethod ?? "",
                width = meta.Width,
                height = meta.Height,
                ffmpeg_exit_code = exitCode
            });
        }
        else
        {
            rec.State = RecState.failed;
            BumpStateVersion();
            rec.Error = rec.Warnings.Count > 0 ? string.Join("; ", rec.Warnings) : "ffmpeg produced invalid output";
            _audit.Log("recording.failed", new
            {
                recording_id = rec.Id,
                backend = rec.BackendType,
                error = rec.Error,
                container = meta.Container ?? "mp4",
                codec = meta.Codec ?? "h264",
                capture_method = meta.CaptureMethod ?? "",
                stage = meta.Stage ?? "",
                hresult = meta.Hresult ?? "",
                ffmpeg_exit_code = exitCode,
                size_bytes = meta.SizeBytes,
                duration_seconds = meta.DurationSeconds,
                stderr_excerpt = rec.StderrExcerpt ?? ""
            });
        }

        tray.SetIdle(rec);
    }

    public object Stop(string id, string reason)
    {
        var rec = Get(id);
        if (rec.State is RecState.completed or RecState.failed or RecState.cancelled)
            return StatusObj(rec);

        rec.State = RecState.stopping;
        BumpStateVersion();
        _audit.Log("recording.stopping", new { recording_id = rec.Id, reason });

        var meta = rec.Backend?.Stop() ?? new OutputMeta();
        int exitCode = rec.Backend?.ExitCode ?? -1;

        FinalizeRecording(rec, meta, exitCode, natural: false, _tray!);
        return new
        {
            recording_id = rec.Id,
            status = rec.State.ToString(),
            output = OutputObj(rec, meta)
        };
    }

    public object GetStatus(string id)
    {
        var rec = Get(id);
        var elapsed = rec.State == RecState.recording
            ? (int)(DateTime.UtcNow - rec.StartedAtUtc).TotalSeconds : 0;

        // For WGC still-frame the actual file lives in meta.OutputPath rather
        // than rec.OutputPath (which is the FFmpeg output path). Pick the
        // right one so we read the correct bytes, container, codec for callers.
        var meta = rec.LastMeta;
        string actualPath = meta?.OutputPath ?? rec.OutputPath;

        string container = meta?.Container ?? string.Empty;
        string codec = meta?.Codec ?? string.Empty;

        return new
        {
            recording_id = rec.Id,
            status = rec.State.ToString(),
            source = new { type = rec.SourceType, title = rec.SourceTitle },
            backend = rec.BackendType,
            started_at = rec.StartedAtUtc == default ? null : Iso(rec.StartedAtUtc),
            completed_at = rec.CompletedAtUtc.HasValue ? Iso(rec.CompletedAtUtc.Value) : null,
            elapsed_seconds = elapsed,
            audio = new { microphone = new { enabled = rec.Microphone } },
            output = new
            {
                path = actualPath,
                bytes_written = SafeSize(actualPath),
                duration_seconds = meta?.DurationSeconds ?? 0,
                container,
                codec,
                width = meta?.Width ?? 0,
                height = meta?.Height ?? 0,
                capture_method = meta?.CaptureMethod ?? "",
                ffmpeg_exit_code = rec.ExitCode
            },
            warnings = rec.Warnings.ToArray(),
            stderr_excerpt = rec.StderrExcerpt ?? "",
            nested = new
            {
                role = rec.NestedRole ?? "none",
                session_id = rec.NestedSessionId ?? "",
                parent_recording_id = rec.ParentRecordingId ?? "",
                is_parent = rec.IsNestedParent
            }
        };
    }

    public object GetOutput(string id)
    {
        var rec = Get(id);
        // Prefer the meta already produced by the backend (e.g. WGC still-frame
        // writes PNG path into meta.OutputPath). Fall back to probing the
        // FFmpeg output path for legacy recordings that have no LastMeta yet.
        var meta = rec.LastMeta;
        if (meta == null)
        {
            meta = FfmpegCaptureBackend.Probe(rec.OutputPath);
        }
        return new
        {
            recording_id = rec.Id,
            output = OutputObj(rec, meta, full: true),
            warnings = rec.Warnings.ToArray(),
            stderr_excerpt = rec.StderrExcerpt ?? "",
            nested = new
            {
                role = rec.NestedRole ?? "none",
                session_id = rec.NestedSessionId ?? "",
                parent_recording_id = rec.ParentRecordingId ?? "",
                is_parent = rec.IsNestedParent
            }
        };
    }

    public object GetConfirmation(string id)
    {
        if (!_confs.TryGetValue(id, out var c))
            throw new ApiException(404, "RECORDING_NOT_FOUND", "Confirmation not found");
        return new { ConfirmationId = c.Id, Status = c.Status, RecordingId = c.RecordingId };
    }

    /// <summary>
    /// Long-polling wait for confirmation status change.
    /// Returns immediately if status != since_status or if wait_ms expires.
    /// Uses case-insensitive status comparison and deadline-based remaining time.
    /// </summary>
    public object GetConfirmationWait(string id, string sinceStatus, int waitMs)
    {
        if (!_confs.TryGetValue(id, out var c))
            throw new ApiException(404, "RECORDING_NOT_FOUND", "Confirmation not found");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool timedOut = WaitForStateChange(() => !string.Equals(c.Status, sinceStatus, StringComparison.OrdinalIgnoreCase), waitMs);
        sw.Stop();

        bool changed = !string.Equals(c.Status, sinceStatus, StringComparison.OrdinalIgnoreCase);
        int? nextPollHintMs = string.Equals(c.Status, "pending", StringComparison.OrdinalIgnoreCase) ? 500 : null;

        return new
        {
            ConfirmationId = c.Id,
            Status = c.Status,
            RecordingId = c.RecordingId,
            Wait = new { RequestedMs = waitMs, ElapsedMs = (int)sw.ElapsedMilliseconds, TimedOut = timedOut },
            NextPollHintMs = nextPollHintMs
        };
    }

    /// <summary>
    /// Long-polling wait for recording status change.
    /// Returns immediately if status != since_status or if wait_ms expires.
    /// Uses case-insensitive status comparison and deadline-based remaining time.
    /// </summary>
    public object GetStatusWait(string id, string sinceStatus, int waitMs)
    {
        var rec = Get(id);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool timedOut = WaitForStateChange(() => !string.Equals(rec.State.ToString(), sinceStatus, StringComparison.OrdinalIgnoreCase), waitMs);
        sw.Stop();

        return BuildStatusWaitResponse(rec, waitMs, (int)sw.ElapsedMilliseconds, timedOut);
    }

    /// <summary>
    /// Shared wait logic: blocks on _lock using Monitor.Wait with remaining time.
    /// _stateVersion is only a wake-up signal; after waking, the predicate is re-evaluated.
    /// This prevents unrelated state changes from causing premature returns.
    /// </summary>
    private bool WaitForStateChange(Func<bool> predicate, int waitMs)
    {
        if (predicate())
            return false;

        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);

        lock (_lock)
        {
            while (!predicate())
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    return true;

                Monitor.Wait(_lock, remaining);
                // After waking (spurious or PulseAll), re-evaluate predicate.
                // Do NOT check _stateVersion; unrelated changes must not break the loop.
            }
        }

        return false;
    }

    private object BuildStatusWaitResponse(Recording rec, int requestedMs, int elapsedMs, bool timedOut)
    {
        var elapsed = rec.State == RecState.recording
            ? (int)(DateTime.UtcNow - rec.StartedAtUtc).TotalSeconds : 0;
        var meta = rec.LastMeta;
        string actualPath = meta?.OutputPath ?? rec.OutputPath;

        // next_poll_hint_ms: null for terminal states, 1000 for active states.
        bool isTerminal = rec.State is RecState.completed or RecState.failed or RecState.cancelled
            or RecState.rejected or RecState.expired;
        int? nextPollHintMs = isTerminal ? null : 1000;

        return new
        {
            RecordingId = rec.Id,
            Status = rec.State.ToString(),
            Source = new { Type = rec.SourceType, Title = rec.SourceTitle },
            Backend = rec.BackendType,
            StartedAt = rec.StartedAtUtc == default ? null : Iso(rec.StartedAtUtc),
            CompletedAt = rec.CompletedAtUtc.HasValue ? Iso(rec.CompletedAtUtc.Value) : null,
            ElapsedSeconds = elapsed,
            Audio = new { Microphone = new { Enabled = rec.Microphone } },
            Output = new
            {
                Path = actualPath,
                BytesWritten = SafeSize(actualPath),
                DurationSeconds = meta?.DurationSeconds ?? 0,
                Container = meta?.Container ?? "",
                Codec = meta?.Codec ?? "",
                Width = meta?.Width ?? 0,
                Height = meta?.Height ?? 0,
                CaptureMethod = meta?.CaptureMethod ?? "",
                FfmpegExitCode = rec.ExitCode
            },
            Warnings = rec.Warnings.ToArray(),
            StderrExcerpt = rec.StderrExcerpt ?? "",
            Nested = new
            {
                Role = rec.NestedRole ?? "none",
                SessionId = rec.NestedSessionId ?? "",
                ParentRecordingId = rec.ParentRecordingId ?? "",
                IsParent = rec.IsNestedParent
            },
            Wait = new { RequestedMs = requestedMs, ElapsedMs = elapsedMs, TimedOut = timedOut },
            NextPollHintMs = nextPollHintMs
        };
    }

    public IEnumerable<object> List() => _recs.Values.Select(r => new
    {
        recording_id = r.Id, status = r.State.ToString(),
        started_at = r.StartedAtUtc == default ? null : Iso(r.StartedAtUtc),
        completed_at = r.CompletedAtUtc.HasValue ? Iso(r.CompletedAtUtc.Value) : null,
        output_path = r.OutputPath,
        nested_role = r.NestedRole ?? "none",
        parent_recording_id = r.ParentRecordingId ?? "",
        nested_session_id = r.NestedSessionId ?? ""
    });

    private void TrySetIdleOnAllDone(ITrayContext tray)
    {
        lock (_lock)
        {
            var anyActive = _recs.Values.Any(r =>
                r.State is RecState.recording or RecState.stopping or RecState.pending_confirmation);
            if (!anyActive)
                tray.SetAllIdle();
        }
    }

    public void StopAllSync(string reason)
    {
        foreach (var r in _recs.Values.Where(r => r.State == RecState.recording))
            try { Stop(r.Id, reason); } catch { }
    }

    private Recording Get(string id) =>
        _recs.TryGetValue(id, out var r) ? r
        : throw new ApiException(404, "RECORDING_NOT_FOUND", $"Recording {id} not found");

    private object StatusObj(Recording r) => new { recording_id = r.Id, status = r.State.ToString() };

    private static object OutputObj(Recording rec, OutputMeta m, bool full = false)
    {
        string actualPath = m.OutputPath ?? rec.OutputPath;
        bool exists = File.Exists(actualPath);
        string container = m.Container ?? "mp4";
        string codec = m.Codec ?? "h264";

        var expectedSecs = rec.DurationSeconds ?? 0;
        var warnings = new List<string>(m.Warnings ?? Array.Empty<string>());

        bool isWgcStillFrame = string.Equals(container, "png", StringComparison.Ordinal) &&
                               string.Equals(codec, "still-frame", StringComparison.Ordinal);

        // Duration warnings are only meaningful for video streams (FFmpeg).
        // WGC still-frame intentionally has DurationSeconds=0.
        if (!isWgcStillFrame)
        {
            if (expectedSecs > 0 && m.DurationSeconds < expectedSecs * 0.5 && m.DurationSeconds > 0)
                warnings.Add($"Actual duration ({m.DurationSeconds:F1}s) is less than expected ({expectedSecs}s). This may indicate a capture issue.");
            if (m.DurationSeconds == 0 && expectedSecs > 0)
                warnings.Add("Duration is 0 - no video content was captured. FFmpeg/gdigrab may have failed silently.");
        }

        if (!full)
            return new { path = actualPath, size_bytes = m.SizeBytes, duration_seconds = m.DurationSeconds, container, codec, warnings };
        return new
        {
            path = actualPath, exists, size_bytes = m.SizeBytes,
            duration_seconds = m.DurationSeconds, created_at = Iso(rec.CompletedAtUtc ?? DateTime.UtcNow),
            container, codec, width = m.Width, height = m.Height, fps = m.Fps,
            capture_method = m.CaptureMethod ?? "",
            command_args = rec.Config?.CommandArgs ?? "",
            backend = rec.BackendType,
            source_type = rec.SourceType,
            warnings
        };
    }

    private static long SafeSize(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }
    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
}
