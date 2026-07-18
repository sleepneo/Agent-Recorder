# Agent Recorder API

Base URL: `http://127.0.0.1:37891/api/v1`

Agent Recorder exposes a localhost HTTP API for local AI agents. Common natural
language recording intents should use the quick recording endpoint. Lower-level
endpoints remain available for precise control.

## Response Envelope

Success:

```json
{
  "ok": true,
  "data": {},
  "request_id": "req_xxx"
}
```

Error:

```json
{
  "ok": false,
  "error": {
    "code": "INVALID_ARGUMENT",
    "message": "...",
    "details": {}
  },
  "request_id": "req_xxx"
}
```

## Authentication

State-changing and sensitive endpoints require:

```http
X-Agent-Recorder-Key: <api-key>
```

When started through the portable CLI, `ensure-running` defaults `data_dir` to
`<package-root>\.local-data` and returns the absolute `api_key_file` path. If
the app or headless host is launched directly without `AGENT_RECORDER_DATA_DIR`,
the default data directory is `%LOCALAPPDATA%\AgentRecorder`.

Agents should use the returned `api_key_file` field instead of assuming a fixed
path.

| Endpoint | Auth |
| --- | --- |
| `GET /capabilities` | No |
| `GET /permissions` | No |
| `GET /displays` | No |
| `GET /windows` | No |
| `GET /windows/active` | No |
| `GET /audio/devices` | No |
| `POST /recordings/quick` | Yes |
| `POST /region-selections` | Yes |
| `POST /recordings` | Yes |
| `GET /recordings` | Yes |
| `GET /recordings/{id}` | Yes |
| `POST /recordings/{id}/stop` | Yes |
| `GET /confirmations/{id}` | Yes |

## Performance Tracing

Recording intents can optionally carry a client-sent timestamp for local latency diagnostics:

```http
X-Agent-Sent-At: 2026-07-15T00:00:00.000Z
```

This value is provided by the agent and is treated as an **untrusted hint**. It is validated for basic plausibility (within -60 seconds to +5 minutes of server time) and stored in a separate `client_hints` field. It does not affect request success or failure, and it is **not** included in server-side latency percentiles or SLO calculations.

Successful recording responses include an optional `performance_trace_id`:

```json
{
  "status": "requires_user_confirmation",
  "confirmation_id": "conf_xxx",
  "recording_id": "rec_xxx",
  "performance_trace_id": "trace_xxx"
}
```

This identifier is also written to the local performance trace file (`<data-dir>\perf\recording-traces.jsonl`) along with stage events such as `intent.accepted`, `confirmation.created`, `confirmation.shown`, `capture.start_requested`, `capture.backend_start_returned`, and `capture.first_frame_observed`. Performance traces are local diagnostic data only; they are separate from the audit log and do not contain API keys, full output paths, window titles, the full FFmpeg command line, raw progress text, or the raw `X-Agent-Sent-At` header value.

`capture.backend_start_returned` only means the capture backend's `Start()` call returned; it is **not** evidence that the first frame has been encoded or written.

`capture.first_frame_observed` is only emitted for the default FFmpeg video path (`display`/`window`/`region`). It is produced exactly once when FFmpeg's `-nostats -progress pipe:1` output reports `frame >= 1`, `total_size > 0`, and the progress group ends normally (`progress=continue` or `progress=end`). It proves that FFmpeg has reported processing at least one video frame and that the output stream has positive bytes, giving an upper-bound latency from local user approval to first observable encoding/muxing progress. It is **not** the exact screen-capture first-frame delivery time, not a physical disk-flush guarantee, and not evidence that the MP4 is playable or has passed output validation. If FFmpeg never emits a qualifying progress group, this event may be absent.

The current trace events cover the request-to-backend-start and first-frame-progress path. Model thinking time is not measured by the server. Cold/warm grouped P50/P95 summaries are exposed via `/capabilities.perf_summary` (see the Capabilities section below).

The `ensure-running` cold/warm handshake can now be reliably correlated with a recording trace through a one-time context. After a successful `ensure-running`, the CLI atomically creates a short-lived context file under `<data-dir>\runtime\ensure-contexts` and returns the following in its JSON output:

- `startup_kind`: `cold` (service was started this time) or `warm` (an existing service was reused)
- `ensure_elapsed_ms`: total wall-clock time of this `ensure-running` handshake in milliseconds
- `ensure_context_id`: a one-time context ID such as `ensure_<32 hex chars>`; only present when `ensure_context_available=true`
- `ensure_context_header`: always `X-Agent-Recorder-Ensure-Context`; only present when `ensure_context_available=true`
- `ensure_context_available`: `true` if the context file was created, `false` if creation failed but ensure-running still succeeded

The agent should forward this header on the very next recording creation request:

```http
X-Agent-Recorder-Ensure-Context: ensure_<32 hex chars>
```

Both `POST /api/v1/recordings` and `POST /api/v1/recordings/quick` accept this optional header. The server uses only the ID from the header to read and one-time-consume the local context; the header is never interpreted as a file path. The trusted `cold`/`warm` label, this handshake's `ensure_elapsed_ms`, and the service startup time `service_startup_elapsed_ms` all come from the server-side consumption of the local context, not from any client-supplied header.

Difference between `startup_elapsed_ms` and `ensure_elapsed_ms`:

- `startup_elapsed_ms` is the service process startup-to-ready time; for `warm` it is the original startup time of the reused service, not the current handshake time.
- `ensure_elapsed_ms` is the full wall-clock time of this `ensure-running` call from entry until service identity is verified and the result is ready to return, covering both `cold` and `warm`.

If the context is missing, expired, malformed, fails the service-instance identity check (PID + `ready_at`), fails deletion/claim, or has already been consumed, the server does not write trusted cold/warm fields and does not affect the API status code, confirmation, Consent Invariant, or recording outcome; the recording intent still proceeds through the normal confirmation path. On consumption failure, the trace may contain only `ensure_context_status` (one of `missing`, `invalid`, `expired`, `instance_mismatch`, `reused`, or `unavailable`) without sensitive paths or exception text. For concurrent or duplicate consumption of the same ID, only one trace receives `consumed` and trusted startup fields; the others receive `reused` or `missing`.

The context file is written using a random temp file in the same directory and atomically moved into place; temp files are cleaned up on failure paths. Both the context files and the in-memory consumption tombstones have a default TTL of 5 minutes and are bounded by a count limit so they do not grow without bound. Error results from `ensure-running` omit `startup_kind`, `ensure_elapsed_ms`, `ensure_context_id`, `ensure_context_header`, and `ensure_context_available`.

Trusted context fields appear as optional top-level fields on subsequent events of the same trace:

- `startup_kind`: `cold|warm`
- `ensure_elapsed_ms`: this ensure-running handshake time in milliseconds
- `service_startup_elapsed_ms`: service startup time in milliseconds (for warm, the original startup time)
- `ensure_context_status`: `consumed` or a failure-reason enum

These fields do not appear inside `client_hints`. The raw `ensure_context_id`, context file path, ready file content, and header literal are never written to the performance JSONL or audit log.

## Capabilities

```http
GET /capabilities
```

The response includes:

- app name/version/platform
- host mode and autostart status
- FFmpeg resolution and prewarm status
- recording source support: `display`, `window`, `region`
- audio capability declaration (`recording.audio_capabilities`)
- quick recording endpoint and recipes
- safety and auth policy
- readiness data when available
- performance summary (`perf_summary`) with cold/warm P50/P95 statistics

Audio support (microphone and system audio) is currently **not implemented**. The legacy `recording.audio` array is preserved for backward compatibility and is always empty. Sending `audio.microphone.enabled=true` or `audio.system_audio.enabled=true` returns `CAPABILITY_NOT_IMPLEMENTED`.

Stop controls are reported under `interaction.stop_controls`:

```json
{
  "interaction": {
    "stop_controls": {
      "floating_button": true,
      "tray_stop": true,
      "global_hotkey": {
        "supported": true,
        "registered": true,
        "gesture": "Ctrl+Shift+F10",
        "behavior": "stop_all_active_recordings"
      }
    }
  }
}
```

- `floating_button`: whether a floating stop button is shown for each active recording.
- `tray_stop`: whether the tray menu provides a stop entry.
- `global_hotkey.supported`: whether the host supports a global stop hotkey.
- `global_hotkey.registered`: whether the hotkey was successfully registered.
- `global_hotkey.gesture`: the human-readable hotkey gesture.

Quick recipe fields:

```json
{
  "interaction": {
    "quick_recording_supported": true,
    "quick_recording_endpoint": "/api/v1/recordings/quick",
    "quick_recipes": [
      {
        "name": "record_primary_display",
        "target_type": "primary_display",
        "endpoint": "/api/v1/recordings/quick",
        "method": "POST",
        "request_template": { "target": { "type": "primary_display" } },
        "available": true,
        "unavailable_reason": null
      },
      {
        "name": "record_active_window",
        "target_type": "active_window",
        "endpoint": "/api/v1/recordings/quick",
        "method": "POST",
        "request_template": { "target": { "type": "active_window" } },
        "available": true,
        "unavailable_reason": null
      },
      {
        "name": "record_selected_region",
        "target_type": "selected_region",
        "endpoint": "/api/v1/recordings/quick",
        "method": "POST",
        "request_template": { "target": { "type": "selected_region" } },
        "available": true,
        "unavailable_reason": null
      },
      {
        "name": "record_last_region",
        "target_type": "last_region",
        "endpoint": "/api/v1/recordings/quick",
        "method": "POST",
        "request_template": { "target": { "type": "last_region" } },
        "available": true,
        "unavailable_reason": null
      }
    ]
  }
}
```

### Performance summary

The response includes `perf_summary`, a bounded, read-only statistical summary of recent recording traces. It is diagnostic data, not an audit log, and never contains trace IDs, recording IDs, confirmation IDs, output paths, API keys, or context headers.

Only traces that consumed a trusted `ensure-running` context (`startup_kind` of `cold` or `warm`, `ensure_context_status=consumed`, non-negative `ensure_elapsed_ms`) are grouped. All other traces are counted as `unclassified_trace_count`.

```json
{
  "perf_summary": {
    "schema_version": 1,
    "status": "available",
    "generated_at": "2026-07-18T00:00:00.000Z",
    "window": {
      "max_traces_per_group": 50,
      "source": "local_rolling_jsonl"
    },
    "quality": {
      "malformed_line_count": 0,
      "unsupported_schema_count": 0,
      "discarded_sample_count": 0,
      "unclassified_trace_count": 3,
      "reason_code": null
    },
    "groups": {
      "cold": {
        "trace_count": 4,
        "quality": "preliminary",
        "metrics": {
          "ensure_running_ms": { "sample_count": 4, "p50": 730.0, "p95": 820.0 },
          "service_startup_ms": { "sample_count": 4, "p50": 165.0, "p95": 180.0 },
          "request_to_confirmation_shown_ms": { "sample_count": 4, "p50": 120.0, "p95": 150.0 },
          "confirmation_shown_to_approved_ms": { "sample_count": 3, "p50": 250.0, "p95": 310.0 },
          "approved_to_first_frame_progress_ms": { "sample_count": 4, "p50": 740.0, "p95": 810.0 },
          "request_to_first_frame_progress_ms": { "sample_count": 4, "p50": 1120.0, "p95": 1280.0 }
        }
      },
      "warm": {
        "trace_count": 12,
        "quality": "representative",
        "metrics": {}
      }
    }
  }
}
```

Status values:

- `available`: at least one qualifying `cold` or `warm` trace exists, and the current refresh did not hit any read boundary, parse fault, or data loss.
- `no_data`: the perf files are missing/empty, or no qualifying trace exists, and the current refresh did not encounter any fault. The full structure and zero counts are still returned.
- `degraded`: a read/parse failure occurred or some valid samples were discarded; partial statistics that were already accumulated are still returned. A stable `reason_code` is provided. No exception text, file paths, or IDs are leaked.

Status precedence (lowest to highest): `no_data` < `available` < `degraded`. Any data loss during the current refresh (boundary, malformed line, unsupported schema, or discarded context/event sample) forces `degraded`, even when valid traces remain.

Read boundaries and privacy: the provider scans `<data-dir>\perf\recording-traces.jsonl` and its rolled history files `.1.jsonl`, `.2.jsonl`, `.3.jsonl` read-only, with the following multi-dimensional boundaries to prevent a single large or malformed file from impacting the service:

| Boundary | Default | Note |
| --- | --- | --- |
| File count | 4 | Current base file + up to 3 rolled history files |
| Bytes per file | 5 MiB | Counted as UTF-8 bytes, including the newline |
| Total bytes | 20 MiB | Cumulative UTF-8 bytes across files |
| Distinct traces | 10 000 | Counted by unique `trace_id`, not event lines |
| Event lines | 100 000 | Cumulative raw lines across files |
| Line length | 1 MiB | UTF-8 byte limit per line. The limit applies to the line body plus its terminator (LF, CRLF, or CR); a final line without a terminator counts only its body bytes. A leading UTF-8 BOM is not counted toward the line length but is counted toward the per-file and total byte limits. Over-long lines trigger `read_boundary_reached` and stop scanning. Invalid UTF-8 sequences are handled safely. |
| Traces per group | 50 | Each cold/warm group keeps only the 50 most recent traces |

When any boundary is reached, scanning stops, but valid traces and metrics already processed are retained and returned with `status=degraded` and `reason_code` typically `read_boundary_reached`. Boundary values are exposed only through public fields such as `window.max_traces_per_group`; absolute paths are never returned.

Duplicate-event selection: when the same event for the same trace appears in multiple rolled files, the provider selects the earliest legal `elapsed_from_intent_ms`, independent of file enumeration order. If an invalid value (NaN, Infinity, negative, or above the 2-hour sanity bound) is read first and a legal value is read later, the legal value replaces it. When `elapsed` is equal, the earlier `timestamp_utc` is used as a deterministic tie-breaker.

Context-metric validation and conflict detection: both `ensure_elapsed_ms` and `service_startup_elapsed_ms` must be finite, non-negative, and not exceed the 2-hour (7,200,000 ms) data-corruption guard bound. An invalid `ensure_elapsed_ms` prevents the trace from entering the cold/warm groups; an invalid `service_startup_elapsed_ms` skips only that metric sample while keeping the trace grouped. Both are counted in `discarded_sample_count`.

In addition, the provider enforces order-independent consistency for the context fields `startup_kind`, `ensure_context_status`, `ensure_elapsed_ms`, and `service_startup_elapsed_ms`. Repeating the same non-empty value is allowed, but seeing two different non-empty values for the same trace marks a context conflict. Conflicted traces are excluded from the cold/warm groups, counted in `unclassified_trace_count`, and increment `discarded_sample_count`; the summary becomes `degraded`/`partial_data` when other valid traces remain. Missing a value in some events and present in others is not a conflict; only multiple distinct actual values conflict.

Caching and fault policy: results are cached for 10 seconds with single-threaded concurrent refresh. A normal boundary partial result is returned as-is and may replace the cache; it does **not** trigger stale-cache fallback. Only a true file open/read failure (for example, an `IOException` while opening the file) causes the provider to return the most recent cached snapshot as a deep-copied stale snapshot with `status=degraded` and `reason_code=stale_snapshot`. The stale response's `generated_at` reflects the current request time, while the statistics come from the cached snapshot. If no cache exists when a read failure occurs, the provider returns an all-zero `degraded` summary with `reason_code=read_error` (or `unexpected_provider_error` for an internal fault). `ApiServer` also wraps `GET /capabilities` in a final try/catch that returns a `degraded` summary with `reason_code=provider_error`, ensuring `/capabilities` always returns HTTP 200.

Common `reason_code` values:

| reason_code | Meaning |
| --- | --- |
| `read_boundary_reached` | A per-file/total-byte/trace/event-line/line-length boundary was reached |
| `read_error` | File open/read failed (for example, the path points to a directory) |
| `partial_data` | Malformed lines or unsupported schema versions were present, without a specific boundary |
| `stale_snapshot` | Refresh failed and a cached snapshot was returned |
| `unexpected_provider_error` | An unexpected internal provider error occurred with no cache |
| `provider_error` | `ApiServer` caught a provider exception (appears only in the HTTP response) |

Group `quality` is a data-quality label, not a performance SLO:

- `preliminary`: fewer than 20 traces in the group.
- `representative`: 20 or more traces in the group.

Metrics are only present when at least one valid paired sample exists. Each metric has its own `sample_count`. Latencies are computed with the nearest-rank percentile method (`rank = ceil(P/100 * N)`, 1-indexed). Values are rounded to one decimal place.

Metric semantics:

- `ensure_running_ms`: trusted `ensure_elapsed_ms` from the consumed context.
- `service_startup_ms`: trusted `service_startup_elapsed_ms` from the consumed context. For `warm`, this is the original startup time of the reused service, not the current handshake.
- `request_to_confirmation_shown_ms`: `confirmation.shown - intent.accepted`.
- `confirmation_shown_to_approved_ms`: `confirmation.approved - confirmation.shown`; only approved paths contribute.
- `approved_to_first_frame_progress_ms`: `capture.first_frame_observed - confirmation.approved`. The name intentionally includes "progress": it is an upper-bound latency to the first observable encoding/muxing progress reported by FFmpeg, not the physical screen-capture first frame or a disk-flush guarantee.
- `request_to_first_frame_progress_ms`: `capture.first_frame_observed - intent.accepted`; covers the local software path after the request is accepted, excluding agent thinking time and any time before `ensure-running`.

`client_hints.agent_to_server_hint_ms` and other agent-supplied timings are never included in server-side percentiles.

### Context Snapshot

The response includes a `context` object that provides a snapshot of system state, reducing the need for separate calls to `/displays`, `/windows`, and `/windows/active`:

```json
{
  "context": {
    "snapshot_at": "2026-07-07T09:30:00.000Z",
    "displays": {
      "available": true,
      "count": 2,
      "primary_display_id": "display_1",
      "virtual_bounds": { "x": -1920, "y": 0, "width": 3840, "height": 1080 },
      "items": [
        {
          "id": "display_1",
          "name": "Display 1",
          "is_primary": true,
          "bounds": { "x": 0, "y": 0, "width": 1920, "height": 1080 },
          "scale_factor": 1.0
        }
      ],
      "error": null
    },
    "windows": {
      "available": true,
      "active": {
        "id": "window_123456",
        "title": "ChatGPT - Chrome",
        "app_name": "chrome.exe",
        "process_id": 1234,
        "is_minimized": false,
        "bounds": { "x": 10, "y": 20, "width": 1200, "height": 800 }
      },
      "visible_count": 8,
      "items_sample": [
        {
          "id": "window_123456",
          "title": "ChatGPT - Chrome",
          "app_name": "chrome.exe",
          "process_id": 1234,
          "is_active": true,
          "is_minimized": false,
          "bounds": { "x": 10, "y": 20, "width": 1200, "height": 800 }
        }
      ],
      "sample_limit": 10,
      "error": null
    },
    "last_selected_region": {
      "available": true,
      "display_id": "display_1",
      "coordinate_space": "virtual_screen",
      "bounds": { "x": 100, "y": 150, "width": 800, "height": 600 },
      "updated_at": "2026-07-07T09:30:00.000Z",
      "source": "quick_selected_region"
    }
  }
}
```

**Notes:**
- `displays` and `windows` may return `available: false` with an `error` message if enumeration fails
- `last_selected_region` is `null` if no region has been selected
- `last_selected_region` is persisted to `<data-dir>\state\last-selected-region.json` and survives service restarts
- The API returns 200 even if context enumeration partially fails

## Permissions

```http
GET /permissions
```

Returns the current permission status. Screen capture and output-directory selection are granted locally. Microphone and system audio are not implemented.

```json
{
  "screen_capture": { "status": "granted" },
  "microphone": { "supported": false, "status": "not_implemented" },
  "system_audio": { "supported": false, "status": "not_implemented" },
  "output_directory": {
    "status": "granted",
    "default_path": "C:\\...\\.local-data\\Videos",
    "selection_ui": true
  }
}
```

## Audio Devices

```http
GET /audio/devices
```

Audio input enumeration is not implemented. The endpoint returns an empty `input_devices` array and explicitly reports the unimplemented status.

```json
{
  "status": "not_implemented",
  "microphone_supported": false,
  "input_devices": [],
  "system_audio_supported": false
}
```

## Quick Recording

```http
POST /recordings/quick
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <agent-name>
```

Use this endpoint first for common natural-language intents.

```json
{
  "target": {
    "type": "selected_region",
    "selection_timeout_seconds": 120
  },
  "duration_seconds": 60,
  "video": {
    "fps": 30,
    "quality": "medium"
  },
  "output": {
    "directory": "default",
    "filename_template": "recording-{datetime}"
  }
}
```

`audio.microphone.enabled` must be `false` or omitted; `true` returns `CAPABILITY_NOT_IMPLEMENTED`. `audio.system_audio.enabled` is similarly reserved.

Supported `target.type` values:

| target.type | Behavior |
| --- | --- |
| `primary_display` | Resolve the primary display, then create a recording |
| `active_window` | Resolve the active window, clamp its visible bounds to the virtual desktop, then create a recording |
| `selected_region` | Show local region-selection UI, then create a recording |
| `last_region` | Reuse the last successful region selection, then create a recording without showing the UI |

The selected-region UI covers the virtual desktop and supports dragging,
moving, resizing, precise coordinates, common size presets, edge/window
snapping, and click-to-pick for highlighted visible windows. Holding `Alt`
temporarily disables snapping. The overlay is explicitly kept above ordinary
maximized windows across multi-monitor desktops.

Successful creation returns `requires_user_confirmation`:

```json
{
  "status": "requires_user_confirmation",
  "confirmation_id": "conf_xxx",
  "recording_id": "rec_xxx",
  "performance_trace_id": "trace_xxx",
  "summary": {},
  "quick": {
    "target_type": "selected_region",
    "recording_created": true,
    "resolved_source": {
      "type": "region",
      "display_id": "display_1",
      "coordinate_space": "virtual_screen",
      "bounds": { "x": 100, "y": 100, "width": 800, "height": 600 }
    },
    "requires_user_confirmation": true
  }
}
```

For `active_window`, `resolved_source.bounds` is the visible window bounds
reported by Windows, while `resolved_source.capture_bounds` is the clamped and
normalized region actually passed to the capture backend:

```json
{
  "target_type": "active_window",
  "recording_created": true,
  "resolved_source": {
    "type": "window",
    "window_id": "window_123",
    "title": "Codex",
    "bounds": { "x": 0, "y": 0, "width": 3200, "height": 2050 },
    "capture_bounds": { "x": 0, "y": 0, "width": 3200, "height": 2050 }
  },
  "requires_user_confirmation": true
}
```

If selected-region interaction is cancelled, times out, or is unavailable, no
recording is created:

```json
{
  "status": "selection_cancelled",
  "quick": {
    "target_type": "selected_region",
    "recording_created": false
  }
}
```

`last_region` returns `SOURCE_NOT_FOUND` when no prior selection is available:

```json
{
  "ok": false,
  "error": {
    "code": "SOURCE_NOT_FOUND",
    "message": "No last selected region is available.",
    "details": {
      "suggested_action": "use_selected_region_first"
    }
  }
}
```

## Lower-Level Endpoints

### Displays

```http
GET /displays
```

Returns display IDs, names, primary flag, scale factor, and virtual-screen
bounds.

### Windows

```http
GET /windows?include_minimized=false&include_system_windows=false
GET /windows/active
```

Returns window IDs, titles, process names, active/minimized state, and bounds.
Window bounds prefer DWM visible-frame bounds and fall back to `GetWindowRect`
when DWM data is unavailable.

### Region Selection

```http
POST /region-selections
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "purpose": "recording",
  "timeout_seconds": 120
}
```

This endpoint only asks the user to select a region. The agent must create the
recording separately with `POST /recordings`. Prefer `/recordings/quick` for
common selected-region requests.

### Raw Recording

```http
POST /recordings
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <agent-name>
```

Display source:

```json
{
  "source": { "type": "display", "display_id": "display_1" },
  "stop_condition": { "type": "duration", "seconds": 60 },
  "video": { "fps": 30, "quality": "medium" }
}
```

Audio must be disabled or omitted. Setting `audio.microphone.enabled=true` or `audio.system_audio.enabled=true` returns `CAPABILITY_NOT_IMPLEMENTED`.

Region source:

```json
{
  "source": {
    "type": "region",
    "display_id": "display_1",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 100, "y": 100, "width": 800, "height": 600 }
  },
  "stop_condition": { "type": "duration", "seconds": 60 }
}
```

Nested outer:

```json
{
  "source": { "type": "display", "display_id": "display_1" },
  "stop_condition": { "type": "duration", "seconds": 300 },
  "nested": { "role": "outer", "session_id": "session_001" }
}
```

Nested inner:

```json
{
  "source": { "type": "window", "window_id": "window_123" },
  "stop_condition": { "type": "duration", "seconds": 60 },
  "nested": {
    "role": "inner",
    "parent_recording_id": "rec_outer",
    "session_id": "session_001"
  }
}
```

### Preflight checks

`POST /recordings` runs a **before-confirmation** preflight before creating the pending confirmation:

- Output directory is writable.
- Output drive has enough free space.
- FFmpeg / FFprobe are available.
- Capture bounds are valid (positive, even, ≥32×32, and overlap the virtual screen).

If this preflight fails, the API returns 400 immediately and no confirmation is created. The response contains a stable `error.code` and `error.details.suggested_action`:

```json
{
  "ok": false,
  "error": {
    "code": "OUTPUT_DIRECTORY_UNWRITABLE",
    "message": "Output directory is not writable: ...",
    "details": {
      "suggested_action": "choose_another_output_directory",
      "stage": "before_confirmation"
    }
  },
  "request_id": "req_xxx"
}
```

After the user approves and before `StartCapture`, a **before-start** preflight re-runs the same checks and also verifies the target window/display is still available. If the re-check fails, the recording transitions to `failed`, `warnings` includes `preflight_failed: <ERROR_CODE>`, the audit log records `recording.preflight_failed`, and the tray shows a local error balloon. This prevents empty recordings when the target window is closed or minimized during confirmation.

Common preflight error codes:

| error_code | scenario | suggested_action |
| --- | --- | --- |
| `OUTPUT_DIRECTORY_UNWRITABLE` | Output directory cannot be created or written | `choose_another_output_directory` |
| `INSUFFICIENT_DISK_SPACE` | Free space below safety threshold | `free_disk_space_or_choose_another_directory` |
| `ENCODER_UNAVAILABLE` | FFmpeg or FFprobe not found | `check_ffmpeg_files_or_reinstall_package` |
| `SOURCE_NOT_FOUND` | Target window/display disappeared | `choose_source_again` |
| `SOURCE_UNAVAILABLE` | Target window minimized, too small, or off-screen | `restore_or_move_window_then_retry` |

## Confirmation And Status

### Local Confirmation Flow

When a recording requires confirmation, Agent Recorder shows a local confirmation form (non-blocking modeless window):

- **Confirmation Form**: Displays recording info (source, duration, audio, output path, nested role, recording ID, confirmation ID, timeout). The user approves by explicitly clicking "Approve"; the safe default keeps focus on "Reject", so Enter/Esc/close reject the request.
- **Output directory**: Before approving, the user can click "Change..." to choose the save directory for this recording and optionally remember it as the new default. The API cannot approve the recording or change the confirmation result remotely.
- **Tray Menu**: Right-click tray icon, select "Approve recording" or "Reject recording".

Multiple pending requests enter a **local confirmation queue**, not auto-rejected when there's already a pending confirmation. Queue items are processed in order, next item shows automatically after current completes.

**Queue Features**:
- Tray menu shows queue position, e.g., "Approve recording (1/2)"
- Confirmation form shows current item info, next item shows after close
- User actions affect only current queue head, not subsequent items

**Important**: AI agents cannot approve or reject recordings, only wait for status changes. Use long-polling for efficient waiting.

### Immediate Queries

```http
GET /confirmations/{confirmation_id}
GET /recordings/{recording_id}
GET /recordings/{recording_id}/output
POST /recordings/{recording_id}/stop
```

### Long-Polling (Recommended)

Wait for status changes instead of frequent short polling:

```http
GET /confirmations/{confirmation_id}?wait_ms=25000&since_status=pending
GET /recordings/{recording_id}?wait_ms=25000&since_status=recording
```

Parameters:

| Parameter | Description |
|-----------|-------------|
| `wait_ms` | Maximum wait in milliseconds (max 25000) |
| `since_status` | Known status at request time (case-insensitive) |

Behavior:

- If current status differs from `since_status`: return immediately
- If current status equals `since_status`: wait until change or timeout
- On timeout, return current status without error

Long-polling response includes additional fields:

```json
{
  "confirmation_id": "conf_xxx",
  "status": "approved",
  "recording_id": "rec_xxx",
  "wait": {
    "requested_ms": 25000,
    "elapsed_ms": 3200,
    "timed_out": false
  },
  "next_poll_hint_ms": null
}
```

```json
{
  "recording_id": "rec_xxx",
  "status": "completed",
  "stop_reason": "duration_reached",
  "elapsed_seconds": 300,
  "output": { "path": "...", "duration_seconds": 300.0 },
  "wait": {
    "requested_ms": 25000,
    "elapsed_ms": 15200,
    "timed_out": false
  },
  "next_poll_hint_ms": null
}
```

`elapsed_seconds` semantics:

- Wall-clock seconds from capture start to now (for active recordings) or to capture end (for terminal recordings), truncated to a non-negative integer.
- Returns `0` if capture has not actually started (e.g. `created`, `pending_confirmation`, `rejected`, `expired`).
- For active recordings (`recording`, `stopping`) it grows with each query.
- For terminal recordings it is computed from `completed_at` and remains stable across repeated queries.
- `output.duration_seconds` is the media file duration from ffprobe; the two may differ slightly due to encoding, rounding, and backend behavior. Do not use `duration_seconds` as a substitute for `elapsed_seconds`.

New fields:

| Field | Description |
|-------|-------------|
| `elapsed_seconds` | Wall-clock seconds from capture start to now (active) or to `completed_at` (terminal). `0` before capture starts. Not equal to `output.duration_seconds`. |
| `wait` | Wait info object |
| `wait.requested_ms` | Requested wait duration in milliseconds |
| `wait.elapsed_ms` | Actual wait duration in milliseconds |
| `wait.timed_out` | Whether returned due to timeout (`false` = immediate or early return, `true` = timeout) |
| `next_poll_hint_ms` | Suggested polling interval; `null` for terminal states, `500` for confirmation pending, `1000` for recording active |
| `stop_reason` | Termination reason: `duration_reached` for natural completion, `floating_button`, `tray_menu`, `global_hotkey`, `user_requested`, etc. Meaningful in terminal states. |

`since_status` comparison is case-insensitive.

Recommended usage:

1. Use long-polling `wait_ms=25000&since_status=pending` for confirmations
2. Use long-polling `wait_ms=25000&since_status=<last_status>` for recordings
3. After timeout, follow `next_poll_hint_ms` or retry long-polling
4. Stop polling when status reaches terminal states

Recording states:

| State | Meaning |
| --- | --- |
| `pending_confirmation` | Waiting for local user confirmation |
| `recording` | Recording is active |
| `stopping` | Stop requested |
| `completed` | Recording completed |
| `failed` | Recording failed (including preflight re-check failures and backend errors) |
| `cancelled` | Recording cancelled |
| `rejected` | User rejected the confirmation |
| `expired` | Confirmation timed out |

Terminal-state responses also include `stop_reason`:

- `duration_reached`: natural completion when the planned duration elapses;
- `floating_button`, `tray_menu`, `global_hotkey`: user stopped via local controls;
- `user_requested`: API stop with no specific reason supplied.

When the user initiates a stop and the output is basically valid (non-zero duration, reasonable size, encoder exit code 0), the recording ends in `completed` even if the actual duration is shorter than planned. Real output defects such as zero duration, tiny file size, or a non-zero encoder exit code still result in `failed`.

HTTP confirmation approval is intentionally blocked:

```http
POST /confirmations/{id}/approve
```

returns `405 METHOD_NOT_ALLOWED`. The local user must confirm via local UI.

## Recording Bundle

Successful FFmpeg MP4 recordings automatically produce a structured bundle next
to the video file. For `D:\Videos\demo.mp4` the bundle is:

```text
D:\Videos\demo.bundle\
  metadata.json
  thumbnail.jpg
  first_frame.png
  last_frame.png
  marks.json
```

Bundle generation is best-effort: a failure does **not** change the recording
state from `completed` to `failed`, and the original MP4 remains the primary
output.

All recording resource responses now include a top-level `bundle` object:

```json
{
  "recording_id": "rec_xxx",
  "status": "completed",
  "bundle": {
    "bundle_version": 1,
    "status": "ready",
    "path": "D:\\Videos\\demo.bundle",
    "contents": [
      { "name": "metadata.json", "media_type": "application/json", "size_bytes": 1234 },
      { "name": "thumbnail.jpg", "media_type": "image/jpeg", "size_bytes": 4567 },
      { "name": "first_frame.png", "media_type": "image/png", "size_bytes": 8901 },
      { "name": "last_frame.png", "media_type": "image/png", "size_bytes": 9012 },
      { "name": "marks.json", "media_type": "application/json", "size_bytes": 120 }
    ],
    "error_code": null
  }
}
```

Bundle status values:

| Status | Meaning |
| --- | --- |
| `pending` | Recording has not completed successfully yet. `path` is `null` and `contents` is empty. |
| `generating` | Main video passed validation; the five files are being produced. |
| `ready` | All five files were generated and atomically published. `path` points to the bundle directory. |
| `failed` | Bundle generation failed after the recording succeeded. `error_code` contains a stable code. |
| `not_applicable` | Recording failed, is a WGC still-frame PNG, or no bundle generator is enabled. |

Stable bundle error codes:

| Code | Meaning |
| --- | --- |
| `bundle_already_exists` | Target bundle directory already exists. |
| `bundle_hash_failed` | SHA-256 hash of the main video could not be computed. |
| `bundle_frame_extract_failed` | FFmpeg frame extraction failed or timed out. |
| `bundle_frame_output_invalid` | Extracted image file is missing or has no valid signature. |
| `bundle_metadata_write_failed` | Could not write `metadata.json`. |
| `bundle_marks_write_failed` | Could not write `marks.json`. |
| `bundle_publish_failed` | Atomic publish from the temp directory failed. |
| `bundle_generation_failed` | Catch-all for unexpected generation failures. |

### `metadata.json`

```json
{
  "bundle_version": 1,
  "recording_id": "rec_xxx",
  "confirmation_id": "conf_xxx",
  "generated_at": "2026-07-18T12:34:56.789Z",
  "source": {
    "type": "region",
    "title": "region:Display 1",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 100, "y": 200, "width": 1280, "height": 720 }
  },
  "recording": {
    "started_at": "2026-07-18T12:34:00.000Z",
    "completed_at": "2026-07-18T12:34:30.100Z",
    "requested_duration_seconds": 30,
    "actual_duration_seconds": 30.1,
    "fps": 30,
    "backend": "ffmpeg-region",
    "stop_reason": "duration_reached",
    "audio_microphone": false,
    "nested_role": "none",
    "nested_session_id": null,
    "parent_recording_id": null
  },
  "media": {
    "path": "D:\\Videos\\demo.mp4",
    "file_name": "demo.mp4",
    "container": "mp4",
    "codec": "h264",
    "width": 1280,
    "height": 720,
    "size_bytes": 1234567,
    "sha256": "64-character-lowercase-hex"
  },
  "audit_correlation": {
    "recording_id": "rec_xxx",
    "confirmation_id": "conf_xxx"
  }
}
```

### `marks.json`

The marks file currently defines the versioned schema only; the `marks` array
is empty until mouse/keyboard mark support is implemented.

```json
{
  "bundle_version": 1,
  "recording_id": "rec_xxx",
  "marks": []
}
```

## Common Error Codes

| Code | Meaning |
| --- | --- |
| `UNAUTHORIZED` | Missing API key |
| `FORBIDDEN` | Invalid API key |
| `INVALID_ARGUMENT` | Request body or parameter is invalid |
| `SOURCE_NOT_FOUND` | Display/window/source is unavailable |
| `SOURCE_UNAVAILABLE` | Source blocked by safety policy |
| `PERMISSION_DENIED` | Output path or operation denied |
| `RECORDING_ALREADY_RUNNING` | Non-nested recording already active |
| `OUTER_RECORDING_ALREADY_EXISTS` | Nested outer already active |
| `INNER_RECORDING_ALREADY_EXISTS` | Nested inner already active |
| `PARENT_NOT_RECORDING` | Nested inner parent is not recording |
| `METHOD_NOT_ALLOWED` | HTTP confirmation approval/rejection is blocked |
