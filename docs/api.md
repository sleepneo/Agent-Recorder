# Agent Recorder API

This reference describes the current source contracts through SQLite v41:
history, health, artifacts, generation digests and read-only retention preview.
The published v0.1.15 package predates those increments; inspect `/capabilities`
on the running host. Generation-time durable media hashes are described at the
end of this reference. Explicit main-media and five-attachment verification have passed independent source acceptance. They share one host content-verification worker; retention deletion is not implemented.

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

### Agent-initiated interactive cold start

`AgentRecorder.Cli.exe ensure-running --json` distinguishes API readiness from
the tray App's active-user interactive desktop proof. Successful tray results
include `desktop_status: "interactive"` and `launch_path` (`direct`,
`task_scheduler_interactive_token`, or `reuse`). The App refuses to acquire its
single-instance mutex or bind the API from an unverified desktop. Recorder
supports direct start/reuse in the current user's verified desktop and a
same-user, explicitly configured task for advanced recovery. It does not
dispatch across Windows account boundaries.

Before calling `ensure-running`, the agent should check whether its host offers
an authorized user-desktop command execution surface. If so, invoke the fixed
CLI command through that host surface. Some agent hosts may provide tool-authorized
execution for a specific command; that is a host invocation mechanism, not a
Recorder CLI option, and does not imply that other agents or hosts have the
same capability. Recorder cannot escape a sandbox or obtain another account's
permissions. If no suitable authorized surface exists, startup remains
fail-closed; a successful API response or an existing process alone does not
prove that the tray UI is visible.

Only when the host has no suitable surface and the isolated process belongs to
the **same Windows user** as the active desktop may an administrator/user
consider the one-time advanced recovery setup:

```text
AgentRecorder.Cli.exe interactive-launch setup --json --app "<full AgentRecorder.App.exe path>" --data-dir "<same data directory used by ensure-running>"
```

Run setup once from that unlocked user's desktop, without elevation. `status`
and `remove` are scoped to the same user. This mechanism does not support
`--agent-sid`; legacy cross-account options and schema-v2 registrations return
`INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED`, and the CLI does not alter legacy
Task Scheduler tasks.

Locked/no-session, wrong-user/session, stale-binary, and mismatched-task states
return errors rather than `ok: true`. A known cross-account caller receives
`INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED` before the CLI reads or changes the
requested data directory. Optional session/desktop details are available only
with `--diagnostics` or the explicit `interactive-launch diagnose` command;
diagnostics exclude window titles, screen content, and API-key material.

Agents should use the returned `api_key_file` field instead of assuming a fixed
path.

| Endpoint | Auth |
| --- | --- |
| `GET /capabilities` | No |
| `GET /health` | Yes |
| `GET /permissions` | No |
| `GET /displays` | No |
| `GET /windows` | No |
| `GET /windows/active` | No |
| `GET /audio/devices` | No |
| `POST /recordings/quick` | Yes |
| `POST /region-selections` | Yes |
| `POST /recordings` | Yes |
| `POST /plans` | Yes |
| `GET /plan-setups/{id}` | Yes |
| `GET /plans/{plan_id}/status` | Yes |
| `GET /plans/{plan_id}/history` | Yes |
| `GET /recordings` | Yes |
| `GET /recordings/{id}` | Yes |
| `POST /recordings/{id}/marks` | Yes |
| `POST /recordings/{id}/stop` | Yes |
| `GET /confirmations/{id}` | Yes |

## On-demand read-only health

Use `GET /api/v1/health` with `X-Agent-Recorder-Key` when diagnosing an environment or a failure. Both App and Headless expose it; `capabilities.health` advertises its endpoint, authentication and read-only nature without sampling. This is optional diagnosis, not a required pre-recording step. Continue to use recordings/history/plans APIs for task status, stop and results. Do not ask a human to open a panel or explain technical status; health does not grant capture permission, repair the environment or authorize retrying a failed recording.

The existing `{ok,data,request_id}` envelope returns HTTP 200 with `data.sample_status` equal to `complete`, `busy`, `timeout`, `failed` or `not_checked`. Missing/wrong keys return 401 before sampling, using existing `UNAUTHORIZED`/`FORBIDDEN` error codes (other endpoints keep their existing authentication status codes). An unconfigured provider yields `not_checked`. One three-second deadline covers owner dispatch and probes. Concurrent API/UI reads share one quota. Timeout ends waiting; an undrained owner callback or a native probe still running retains the quota, and subsequent reads return `busy`.

| Group | Fields and evidence |
| --- | --- |
| `instance` | `version`, `mode`, `pid`, actual `listening_port` (null when not listening), `ready_state`, frozen `data_directory`, `sampled_at`, `evidence_type=runtime_snapshot`. Ready requires this host's live publication receipt, not a stale file or PID check. |
| `desktop` | `ui_required`; `startup` contains static receipt state/time; `current` contains dynamic read-only desktop state/time. Each has `reason_code`, `evidence_type`, and `limitation`. Headless's lack of UI requirement does not establish an interactive desktop. |
| `tools` | `ffmpeg`, `ffprobe`, `wgc_helper`, `audio_helper`: `path`, whitelisted `source`, `state`, `reason_code`, `sampled_at`, `evidence_type=path_only`. A found path does not verify execution. |
| `hotkeys` | `stop`, `chapter`: `supported`, `registered`, whitelisted `gesture`, `policy`, `state`, `reason_code`, `sampled_at`, `evidence_type=owner_snapshot`. Chapter's unregistered idle state is `idle`, required registration failure is `registration_failed`; Headless reports unsupported. |
| `backends` | `window`, `display`, `region`: frozen `policy`, `experimental`, `capture_check=not_checked`, `evidence_type=configuration_only`; `microphone`, `system_audio`: backend `supported`, `device_check=not_checked`, `evidence_type=backend_support_only`. |

Codes and snake_case keys are independent of UI language. No blanket `healthy`/`can_record` field is provided. Partial snapshots mark unsampled probes `not_checked`; unavailable owner facts remain unknown. Secrets, identity/SID, titles, device lists and raw exceptions are excluded. Reading starts no tool/helper process or capture and writes no business data.

This endpoint accepts no parameters. As with parameterless capabilities, syntactically valid unknown query keys are ignored, including `refresh`, `watch` and `timeout`; none changes behavior. The existing parser still rejects duplicate/empty keys or malformed encoding with 400. There is no refresh/watch/long-poll/custom-timeout protocol.

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

`capture.first_frame_observed` is emitted only after backend-specific credible
evidence. On the default FFmpeg video path (`display`/`window`/`region`), it is
produced once when `-progress pipe:1` reports `frame >= 1`, `total_size > 0`,
and a normally completed progress group. This is an upper bound to observable
encoding/muxing progress, not the exact physical capture or disk-flush time. On
the experimental WGC continuous path, it is produced from the authenticated
`FIRST_FRAME` IPC event after a source frame is accepted and copied
successfully. Neither form proves that the final MP4 is playable or has passed
output validation.

The current trace events cover the request-to-backend-start and first-frame-progress path. Model thinking time is not measured by the server. Cold/warm grouped P50/P95 summaries are exposed via `/capabilities.perf_summary` (see the Capabilities section below).

Additional lifecycle events emitted after approval (microphone recordings):

| Event | Emitted when | Notes |
| --- | --- | --- |
| `microphone_prepare_started` | The capture-safe barrier completed and the audio worker is opening the selected microphone. | `preparing` UI is shown; no screen capture yet. |
| `microphone_ready` | The audio worker has produced credible audio samples. | Triggers the 3-2-1 countdown. |
| `countdown_started` | The 3-2-1 countdown UI started. | Screen capture has not started; `elapsed_seconds` remains `0`. |
| `video_first_frame` | The independent video worker reported its first credible frame. | `recording` state and REC UI begin here; `StartedAtUtc` is set. |
| `capture_ended` | Screen capture stopped (duration reached or user-initiated stop). | REC UI is hidden immediately; `finalizing` begins. |
| `finalization_completed` | Audio crop, mux, ffprobe validation and bundle generation finished. | Terminal state (`completed`/`failed`) is published after this. |

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
- chapter-mark API and local-hotkey state (`chapter_marks`)
- durable ordinary-video recording history (`recording_history`), explicitly excluding unattended execution and screenshot series
- bounded one-shot unattended support and its live safety state (`unattended_lease`)

`recording_history` advertises `scope="ordinary_video"`, authenticated list and
detail endpoints, stable keyset pagination (default 20, maximum 100), and the
fact that the history can be read after process restart. It does not claim
coverage of automatic/lease executions or screenshot-series output.

A native `wgc-native-helper.exe`, managed continuous session, and
capture-backend adapter are present in the repository. Guarded selectors can
use this pipeline for eligible 1–60 second, silent display, window, or region
requests only when the corresponding local experiment is explicitly enabled. Availability
is checked without capture, successful evidence is cached briefly, and an
ineligible or unavailable request falls back to FFmpeg. Window mode uses the
actual HWND and reports stable lifecycle failures when the target closes,
minimizes, or changes size. Region mode locks a stable display identity,
revalidates current topology after approval, and crops the display texture on
the GPU before encoding. The self-contained portable package contains one
production helper. The display/region WGC experiment remains internal,
default-off, and cannot be selected through public request fields. The public
API has a separate, explicit fixed-window contract (`required_capture_semantics`
below): it is available only when the non-capturing WGC capability probe passes.

The 1–60 second range is the current controlled acceptance boundary, not a claim
that every GPU, driver, Windows version, or application is compatible. The
explicit window-surface contract is never silently downgraded. For that request,
WGC supplies video and the existing WASAPI/AvSplit path may add system loopback;
the ordinary experimental WGC display/region path remains audio-free. Legacy
window requests without the strict field retain their current FFmpeg behavior.

Microphone recording uses an isolated Windows WASAPI helper process (`AgentRecorder.AudioHelper.exe`) that captures via CoreAudio and writes a temporary WAV. The final MP4 mux step encodes the audio as AAC. For Bluetooth Hands-Free capture endpoints, the helper classifies the transport, discovers the render endpoint belonging to the same device container, starts a silent render stream, and keeps that endpoint alive while capture runs. This establishes the duplex HFP link required by devices such as AirPods Pro and Focal Bathys. The helper reports the selected capture strategy, pairing evidence, render-prime latency, current/max gap, recovery, gap-fill, and discontinuity metrics. Compatibility still depends on the Windows Bluetooth stack, device firmware, and driver. The legacy FFmpeg dshow backend remains available as an explicit diagnostic fallback via `AGENT_RECORDER_AUDIO_BACKEND=dshow`; without this variable the default is `wasapi-helper`.

The `recording.audio` array is preserved for backward compatibility and now reports `["microphone", "system_audio"]`. System audio is public; requests still pass the same local confirmation, preflight, fixed-endpoint, and bounded-recovery boundaries as microphone requests.

`recording.audio_capabilities.microphone` and `.system_audio` both report `supported: true` and a fresh status of `ready`, `no_devices`, or `unavailable`. No device is reported as unsupported merely because enumeration returned an empty list.

`recording.window_surface` reports whether the WGC window runtime probe is
currently ready, its reason code when unavailable, the strict-path 1800-second
maximum, and whether system loopback is currently usable. It also reports the
1-second minimum and that each run requires local confirmation.
`long_run_readiness="bounded_duration_only"` and
`long_run_stress_tested=false` make clear that the capability probe does not
certify 30-minute A/V continuity for a particular window, endpoint, GPU,
driver, or machine; the configured maximum is not a claim of completed long-run
acceptance or complete livestream recording. The
`system_audio_supported` value is true only when WGC window capture, a render
endpoint, and a successful WASAPI helper protocol probe are all available.
When no render endpoint is ready, the helper status is `not_checked`.

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

Chapter-mark capabilities are reported at the response root:

```json
{
  "chapter_marks": {
    "supported": true,
    "endpoint": "/api/v1/recordings/{recording_id}/marks",
    "local_hotkey": {
      "supported": true,
      "registered": false,
      "gesture": "Ctrl+Shift+F11",
      "registration_policy": "while_recording"
    }
  }
}
```

The tray host registers the local hotkey only while at least one recording is
in the exact `recording` state. The headless host reports API support but no
local hotkey.

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

When reliable Windows display numbers are present, `quick_recipes` additionally
contains one `record_windows_display_N` entry per current number. Its
`request_template.target.windows_display_number` is the actual positive JSON
integer for that snapshot, never a string placeholder. If no reliable number
is available, `interaction.windows_display.available` is `false` and no such
recipe is emitted.

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
          "name": "Display 2",
          "windows_display_number": 2,
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

Display selection uses two intentionally separate identifiers. `id` (also called
`display_id` in recording requests) is an opaque API selection token kept for
compatibility; its numeric suffix is not a Windows display number. Each display
item also has `windows_display_number`, an integer when the current Windows GDI
device mapping can be parsed strictly and uniquely, otherwise `null`. The source
is `MONITORINFOEX.szDevice`; the accepted shape is exactly `\\.\DISPLAY<N>` with
a positive ASCII decimal suffix. This mapping was checked against the current
machine and the Windows Settings display-identification view during acceptance;
it is an observation of the current configuration, not a cross-machine or
cross-driver stable identity. When the number is available, `name` follows it
(for example,
`id: "display_1", windows_display_number: 2, name: "Display 2"`). Agents should
match a user's “display N” request by `windows_display_number`, then pass that
item's `id` unchanged to the recording API. If the field is `null`, use
`is_primary`, bounds, and the name to ask the user to disambiguate; never invent
or construct a `display_N` token.

## Permissions

```http
GET /permissions
```

Returns the current permission status. Screen capture and output-directory selection are granted locally. Microphone and system audio are supported; each status honestly reflects availability (`available`, `no_devices`, or `unavailable`). This version does not probe the real Windows microphone or render-endpoint ACL, so it does not report a fake OS `granted` value.

```json
{
  "screen_capture": { "status": "granted" },
  "microphone": { "supported": true, "status": "available" },
  "system_audio": { "supported": true, "status": "available" },
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

Enumerates real microphone input devices via FFmpeg dshow and active render endpoints via CoreAudio. The legacy `status` remains the microphone status; `microphone_status` and `system_audio_status` make both sides explicit. Each status is `ready`, `no_devices`, or `unavailable`; both `microphone_supported` and `system_audio_supported` are `true`. Microphone and render enumeration failures are isolated and the endpoint returns HTTP 200 with the failed side empty.

When microphone audio is requested, Agent Recorder launches an isolated Windows WASAPI helper (`AgentRecorder.AudioHelper.exe`) to capture from the selected CoreAudio endpoint. The device `id` returned by this endpoint is still the FFmpeg dshow alternative name for API compatibility; Agent Recorder maps it to the matching CoreAudio endpoint ID internally. Bluetooth Hands-Free endpoints are transport-classified and can be paired automatically with their same-container render endpoint for duplex prime. Device enumeration and an `active` state still do not guarantee initialization on every driver; pairing, initialization, or runtime sample failures are reported as recording failures. dshow capture remains available as an explicit diagnostic fallback via `AGENT_RECORDER_AUDIO_BACKEND=dshow`.

The device-enumeration parser recognizes both the bundled FFmpeg classic `[dshow]` / `[dshow @ ...] DirectShow audio devices` section and the FFmpeg 8.x tagged `[in#N @ ...] "Name" (audio)` format. Only these two trusted logger prefixes are accepted: classic lines must start with `[dshow]` or `[dshow @ identity]`, and tagged lines must start with `[in#N @ identity]` where `N` is one or more digits and `identity` is non-empty. Quoted friendly/alternative names are parsed with consumed-length validation, so extra text before or after the quoted value (e.g. `prefix "Name" (audio)` or `"Name" (audio) suffix`) is rejected. It decodes FFmpeg `\"` and `\\` escapes in quoted names while preserving ordinary backslashes (e.g. `\wave_{GUID}`). Any malformed or incomplete device record—such as an invalid logger prefix, a missing alternative name, an orphaned alternative, a candidate interrupted by another line, trailing junk after a quoted value, or a conflict between devices and the no-devices marker—causes the entire listing to be treated as unrecognized and reported as `status: "unavailable"`. Lines that lack a trusted logger prefix, including ordinary `warning:` or other logger output, are ignored and never create devices. A classic no-devices marker is only accepted inside a classic audio section that was opened by the trusted `DirectShow audio devices` header; a tagged no-devices marker is accepted directly from a trusted input logger. Complete tagged video records (`(video)` friendly name plus matching alternative) are safely ignored in any order and never interrupt audio enumeration. The parser never returns a partial audio device list. Recognized complete listings (with or without devices) accept normal listing exit-code differences across versions (`1`, `0`, `-2`); otherwise the endpoint returns `ready` or `no_devices`.

The `id` is the FFmpeg dshow alternative name (the example below shows an alternative-name ID without the `audio=` prefix). Callers must pass this `id` back unchanged in `audio.microphone.device_id`; Agent Recorder maps it to the matching CoreAudio endpoint ID for the WASAPI helper. When the dshow fallback is explicitly enabled, Agent Recorder adds the `audio=` prefix internally when building the FFmpeg command line.

```json
{
  "status": "ready",
  "microphone_supported": true,
  "input_devices": [
    {
      "id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}",
      "name": "Microphone (Realtek(R) Audio)",
      "is_default": true,
      "state": "active",
      "is_muted": false,
      "volume_percent": 75
    }
  ],
  "microphone_status": "ready",
  "system_audio_status": "ready",
  "system_audio_supported": true,
  "output_devices": [
    {
      "id": "{0.0.0.00000000}.{RENDER-ENDPOINT}",
      "name": "Speakers",
      "is_default": true,
      "state": "active",
      "direction": "render"
    }
  ]
}
```

Device fields:

| Field | Type | Description |
| --- | --- | --- |
| `id` | string | FFmpeg dshow alternative name. Pass back unchanged. |
| `name` | string | Human-readable device name. |
| `is_default` | boolean \| null | `true` when this is the current CoreAudio multimedia default capture endpoint. Falls back to the dshow-provided default only when CoreAudio cannot be queried. `null` means unknown. |
| `state` | string \| null | `"active"` when the CoreAudio endpoint is active; otherwise `"inactive"`. `null` means unknown. |
| `is_muted` | boolean \| null | `true` when the endpoint is software-muted. `null` means unknown. |
| `volume_percent` | integer \| null | Master volume scalar rounded to `0..100`. `null` means unknown. |

`output_devices` contains only active render endpoints. Its `id` is an opaque
CoreAudio endpoint ID and must be passed back byte-for-byte as
`audio.system_audio.device_id`; omitting that field uses the current Windows
multimedia default output. The output list is ordered with the exact default
first, followed by deterministic name/ID order.

`is_default`, `state`, `is_muted`, and `volume_percent` are read fresh on every request and are **not** cached by the 10-second dshow enumeration cache. Muting, unmuting, or changing the default device is reflected immediately on the next call.

A state of `null` or `"active"` does not block recording. A state of `"inactive"` blocks recording before any region-selection or confirmation UI is shown.

If `is_muted` is `false` and `volume_percent` is below `10`, the confirmation UI shows a low-volume warning. This warning does not block recording, and Agent Recorder does not modify the system volume.

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
  "audio": {
    "microphone": {
      "enabled": true,
      "device_id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}"
    }
  },
  "output": {
    "directory": "default",
    "filename_template": "recording-{datetime}"
  }
}
```

`audio.microphone.enabled` may be `true` to include microphone audio. Omit `audio.microphone.device_id` to auto-select: when exactly one active device is present, or when exactly one device is the current CoreAudio multimedia default, that device is chosen; otherwise provide the input `id` from `GET /api/v1/audio/devices`. Set `audio.system_audio.enabled=true` to capture system audio; omit its `device_id` to use the current Windows multimedia default output, or provide an exact `id` from `output_devices`. The endpoint approved in local confirmation is fixed for that recording. Changing the Windows default output does not switch capture to another endpoint; returning to the approved endpoint uses bounded same-endpoint recovery, with objectively measured gaps represented as silence and reported through continuity/recovery metrics. Microphone and system audio are mutually exclusive.

The default audio capture backend is the isolated WASAPI helper. Set the environment variable `AGENT_RECORDER_AUDIO_BACKEND=dshow` before starting Agent Recorder to use the FFmpeg dshow diagnostic fallback instead; any other value is rejected.

### Strict WGC window-surface recording

Ordinary `POST /api/v1/recordings` accepts the optional top-level
`required_capture_semantics: "window_surface"`. When present it is a hard
requirement: only a fixed `source.type="window"`/`window_id`, normal local
per-run approval, video mode, and a duration stop from 1 to 1800 seconds are
accepted. Audio may be omitted or use `audio.system_audio.enabled=true`; a
microphone is not supported. The capability must be shown as ready by
`GET /api/v1/capabilities` (`recording.window_surface.supported=true`), and a
usable render endpoint is also required for the system-audio variant.

```json
{
  "required_capture_semantics": "window_surface",
  "source": { "type": "window", "window_id": "window_123456" },
  "stop_condition": { "type": "duration", "seconds": 300 },
  "audio": { "system_audio": { "enabled": true } }
}
```

The response first has `status="requires_user_confirmation"`; its summary
must say `capture_semantics="window_surface"`,
`planned_backend="wgc-window-av-split"`, and `selection_fallback=false` for
the system-audio request. The local confirmation names the fixed output
endpoint. System audio is loopback from that Windows render endpoint, not an
audio track exclusive to the selected window. On approval, normal preparation,
audio readiness, countdown, first-frame, stop, and finalization states apply;
success publishes one MP4 with WGC window video and AAC loopback audio.

Strict window recording (including future-window one-shot execution) checks the
actual local fixed volumes behind the frozen output directory, WGC temporary
root, and, with loopback, the A/V temporary directory. Directory mounts and
reparse targets are resolved to volume identities; unsupported or unverifiable
storage fails closed. Admission sums simultaneous video, PCM, mux partial and
publication-copy estimates per volume, using the existing conservative video
estimate (at least 100 MiB or 2 MiB/second) for each video and PCM component
(with 4 KiB WAV header allowance), plus a 256 MiB safety margin per volume. This is
an estimate, not a reservation or quota. Checks repeat before capture helpers
start, including the future authorization execution boundary.

Through capture, mux and final publication, an internal monitor samples about
every five seconds. Any monitored volume below 256 MiB aborts with
`storage_space_low`; exactly 256 MiB is allowed. An unavailable, changed or
unverifiable volume, invalid capacity, or timed-out query uses
`storage_capacity_unavailable`. A query has a two-second deadline. Trusted
storage aborts settle as `failed`, with the reason in `stop_reason`, and publish
no final media. A late sample cannot change a successfully committed run. There
are no API controls to disable the monitor or choose its paths/threshold. No
automatic relocation, deletion of other recordings, retry or authorization
reuse occurs. Ordinary locally confirmed silent video on display/region,
including raw, quick and region Profile requests selecting ffmpeg/ffmpeg-region,
also uses this storage safety. Its owned staging and publication copy are both
beneath the approved output directory: admission budgets two video estimates
plus one watermark on that actual volume, without unused WGC/AV TEMP.
Confirmation, a locally approved directory change and the physical backend-start
boundary recheck capacity; runtime and publication checks include remaining
unwritten copy bytes (already written bytes are not reserved again). Local
directory approval replaces only that Run's layout and monitor; no fallback
directory is chosen. Low capacity and unverifiable capacity use the same stable
storage errors and failed terminal state. This added scope excludes
Standing/Recurring/Required plans, experimental WGC display/region,
screenshot series and other backends. It is neither a quota nor automatic
retention/deletion, and real full-disk/remount desktop acceptance remains pending.

Ordinary locally confirmed video with system audio or microphone, using
ffmpeg-av-split/ffmpeg-region-av-split for display/region, shares the same safety.
It freezes approved output plus the actual application AV temp directory, excluding
unused WGC TEMP. Admission sums video and conservative PCM on AV temp, and mux
partial and publication copy on output, merging roles by actual volume identity
with one 256 MiB watermark per volume. Checks run before confirmation, after
approval, immediately before audio start and at countdown completion before video
start; monitoring covers audio preparation too. Native final publication accounts
only for actual remaining copy bytes, synchronized with capacity samples.
`storage_space_low` and `storage_capacity_unavailable` stop both owned workers
and prevent successful publication when failure wins the existing commit arbiter.
Region Profiles remain silent only; API fields, endpoint binding, A/V coverage and
synchronization tolerances do not change. Point-in-time checks are not a disk
reservation, quota or retention execution; real full-disk/remount acceptance is pending.

Ordinary video requests cannot concurrently occupy the same normalized final path
when either Run is ordinary FFmpeg AV. Registration returns `409 OUTPUT_PATH_IN_USE`;
an approved local directory change that loses this race becomes `rejected` with
`stop_reason=OUTPUT_PATH_IN_USE`, without starting its workers or changing the other
Run. Pending confirmation, preparation, countdown and publication retain ownership;
directory approval cannot rename around an occupied requested filename, even when
the other Run has already written a final file but is still converging.
even a terminal cancellation retains it until owned writer cleanup has ended.
The check uses full paths and ordinal case-insensitive comparison within this engine.
Each AV finalization uses an internally generated private mux filename in the
approved output directory. This does not change copy accounting or publication
receipts, and is not a persistent reservation across plans, hosts, hard links or
directory reparse aliases.

An invalid field, ineligible request, unavailable helper/endpoint, changed HWND
identity/size, or minimized/closed target fails explicitly. In particular,
`WINDOW_SURFACE_UNAVAILABLE` does not fall back to `screen_rectangle`.
`required_capture_semantics` is rejected by quick, screenshot-series, nested,
and plan requests; omitting it preserves all legacy window behavior. See
[Window capture semantics](#window-capture-semantics) for the distinction from
the legacy screen rectangle.

If the selected microphone is muted, the request fails immediately with `409 AUDIO_DEVICE_MUTED` before any region-selection or confirmation UI is shown. Agent Recorder does not automatically unmute the system; the user must unmute the device in Windows sound settings and retry. If the selected device is known to be inactive, the request fails with `503 AUDIO_DEVICE_NOT_AVAILABLE`. A transient CoreAudio failure that leaves the state unknown does not block recording.

Supported `target.type` values:

| target.type | Behavior |
| --- | --- |
| `primary_display` | Resolve the primary display, then create a recording |
| `windows_display` | Resolve one exact Windows display number from the current display snapshot, then create a recording |
| `active_window` | Resolve the active window, clamp its visible bounds to the virtual desktop, then create a recording |
| `selected_region` | Show local region-selection UI, then create a recording |
| `last_region` | Reuse the last successful region selection, then create a recording without showing the UI |

For “record Windows display N”, prefer this one-call target:

```json
{
  "target": { "type": "windows_display", "windows_display_number": 2 },
  "duration_seconds": 30
}
```

`windows_display_number` must be a positive JSON integer. It is the number
shown by Windows “Identify”, not the numeric suffix of the API `display_id`.
The server matches exactly one reliable number from one current display
snapshot and passes that entry's opaque `id` through the existing recording
engine. If the number is missing, unreliable, duplicated, or not present,
the request fails closed with `SOURCE_NOT_FOUND`; refresh `/capabilities` or
`/displays`, or ask the user to disambiguate. It never guesses an API ID.
The successful `quick.resolved_source` includes the resolved API ID,
Windows number, and aligned public display name. Local confirmation is still
required, and the HTTP API cannot approve the request.

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
    "title": "Conversation",
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

### Optional bounded creation wait

Both creation endpoints support an opt-in lifecycle wait:

```http
POST /recordings?wait_for=recording&wait_ms=25000
POST /recordings/quick?wait_for=recording&wait_ms=25000
```

`wait_for` currently accepts only `recording`. If `wait_for=recording` is
present, `wait_ms` defaults to `25000` and must be an integer from `1` through
`25000`. `wait_ms` without `wait_for`, an unknown `wait_for`, or an invalid
`wait_ms` returns `400 INVALID_ARGUMENT` before source resolution, device
enumeration, region selection, confirmation, or capture side effects. Omitting
`wait_for` preserves the ordinary immediate creation response.

The wait begins only after a recording has been created. It observes the first
of the trusted first-frame `recording` state or a terminal state
(`rejected`, `expired`, `cancelled`, `failed`, or `completed`). Approval,
preparing, countdown, backend start, and plan revalidation are not success
milestones; this option never approves, rejects, starts, stops, or shortens a
recording. For `selected_region`, region selection completes before the
recording is created; cancellation or timeout returns the existing quick
response without a synthetic wait object.

When a recording exists, the response keeps the normal creation fields and adds
the current public status, the same canonical object returned by
`GET /recordings/{recording_id}`, and a bounded wait summary:

```json
{
  "status": "pending_confirmation",
  "recording_id": "rec_xxx",
  "confirmation_id": "conf_xxx",
  "summary": {},
  "performance_trace_id": "trace_xxx",
  "recording": {
    "recording_id": "rec_xxx",
    "status": "pending_confirmation"
  },
  "wait": {
    "wait_for": "recording",
    "requested_ms": 25000,
    "elapsed_ms": 25000,
    "timed_out": true,
    "goal_reached": false,
    "terminal": false
  }
}
```

`goal_reached` is true only for the trusted video `recording` milestone;
`terminal` is true only for a terminal state. A timeout returns HTTP 200 with
the current state and does not change the recording. A quick request that
reaches a terminal state before the observer runs returns `terminal: true`, not
`goal_reached: true`. The stable performance-trace event kind is
`creation_recording`; it contains only bounded wait measurements and lifecycle
IDs, never request bodies or output paths. `/capabilities.interaction.creation_wait`
publishes the endpoints, supported value, default/max wait, and
`trusted_first_frame_or_terminal` milestone.

## One-Shot Unattended Fixed-Region Plan

This optional mode is disabled by default. A local user must first enable
unattended mode in the tray safety controls. Every accepted setup then opens a
local region selector and a separate lease-approval dialog. HTTP can request a
setup and observe its state, but it cannot select the region, approve the lease,
or widen an approved scope.

Current limits are deliberately narrow: one run, fixed region, no audio, no
nested recording, FFmpeg region capture, natural wake only, an interactive
desktop, a lease of at most one hour, and a recording of at most ten minutes.

Create or recover an idempotent setup:

```http
POST /plans
X-Agent-Recorder-Key: <api-key>
Idempotency-Key: <stable-key-for-this-request>
Content-Type: application/json
```

```json
{
  "recording_spec": {
    "source": { "type": "fixed_region" },
    "audio": { "mode": "none" },
    "duration_seconds": 30,
    "countdown_seconds": 0,
    "backend": "ffmpeg-region",
    "output": {
      "directory": "D:\\Videos",
      "filename": "scheduled-recording.mp4"
    }
  },
  "schedule": {
    "kind": "once",
    "start_at": "2026-09-17T10:00:00Z",
    "latest_start_at": "2026-09-17T10:02:00Z",
    "planned_end_at": "2026-09-17T10:02:30Z"
  },
  "requested_authorization": {
    "mode": "standing_lease",
    "expires_at": "2026-09-17T10:03:00Z",
    "max_runs": 1,
    "max_duration_seconds": 30
  }
}
```

The request must use an absolute output directory and a file name without path
separators. `start_at < latest_start_at <= planned_end_at`; the latest-start
grace is at most five minutes, the full recording must fit after the latest
start, and lease expiry must be after the planned end and within one hour of
request time.

The server returns `202 Accepted` with a durable `setup_intent_id`, status URL,
version cursor, and the next required local action. Reusing the same
`Idempotency-Key` with the same canonical request returns the existing setup;
reusing it for different content returns `409 IDEMPOTENCY_KEY_REUSED`.

Observe setup and execution state:

```http
GET /plan-setups/{setup_intent_id}
GET /plan-setups/{setup_intent_id}?since_status_version=<cursor>&wait_ms=25000
```

The cursor is opaque and should be returned unchanged. `wait_ms` is bounded to
25 seconds. Responses may include `requires_local_action`, `next_action`,
`reason_code`, `run_id`, `recording_status_url`, `started_at`, and
`completed_at`. Once a `recording_status_url` is present, use the normal
recording status endpoint for media completion and output details.

For `standing_lease` plans, before physical capture the app revalidates the
approved display identity, physical region, session, output target, execution
window, lease state, global safety state, and one-time proof. A mismatch,
revocation, Stop All, session loss, sleep, existing output file, or missed
window fails closed and never falls back to an interactive or differently
scoped recording.

### Durable plan execution status

GET /api/v1/plan-setups/{setup_intent_id} describes creation, local selection,
and approval of a plan. It is not the durable execution result. Use the
authenticated endpoint below to read the plan and its persisted occurrences
after setup or an application restart:

~~~~http
GET /api/v1/plans/{plan_id}/status
X-Agent-Recorder-Key: <api-key>
~~~~

The response uses the normal ok/data/request_id envelope. kind is once, daily,
or weekly; plan_status is the persisted plan state, not proof that recording
succeeded. occurrence_count counts persisted occurrences. latest_occurrence
is ordered by window start descending and then occurrence ID descending.
next_occurrence is the earliest nonterminal persisted occurrence (including an
in-progress run); each contains the plan window, occurrence state/reason,
optional run state/reason, and output evidence. Historical pages are not
included.

Occurrence and Run are read from one SQLite snapshot and must form a reachable
lifecycle pair. `run_created` requires its associated nonterminal Run;
`completed` requires an associated `settled` Run with no terminal reason;
`blocked` with a Run requires matching abnormal terminal reasons. A terminal
occurrence without a Run is accepted only when its persisted shape is valid for
the domain (for example, `missed` cannot have a Run). An inconsistent pair
fails closed with `503 PLAN_STATUS_UNAVAILABLE` instead of being presented as
a successful result.

~~~~json
{
  "ok": true,
  "data": {
    "plan_id": "standing-plan-...",
    "kind": "once",
    "plan_status": "enabled",
    "schedule_exhausted": null,
    "occurrence_count": 1,
    "next_occurrence": {
      "occurrence_id": "occurrence-...",
      "window_start_utc": "2026-10-01T01:00:00Z",
      "window_end_utc": "2026-10-01T01:05:00Z",
      "status": "run_created",
      "terminal_reason_code": null,
      "run_id": "run-...",
      "run": {
        "run_id": "run-...",
        "status": "recording",
        "terminal_reason_code": null
      },
      "output_path": null,
      "output_path_recorded": false,
      "output_file_exists": null
    },
    "latest_occurrence": {
      "occurrence_id": "occurrence-...",
      "window_start_utc": "2026-10-01T01:00:00Z",
      "window_end_utc": "2026-10-01T01:05:00Z",
      "status": "run_created",
      "terminal_reason_code": null,
      "run_id": "run-...",
      "run": {
        "run_id": "run-...",
        "status": "recording",
        "terminal_reason_code": null
      },
      "output_path": null,
      "output_path_recorded": false,
      "output_file_exists": null
    }
  },
  "request_id": "..."
}
~~~~

For one-time plans schedule_exhausted is null. For daily/weekly plans it
reflects the durable schedule cursor. If it is true and next_occurrence is
null, the cursor has no further schedule slots and every materialized
occurrence is terminal; this plan will not automatically run again.

After a successful, validated settlement, `output_path` is the immutable path
reported by the media probe and matched to that occurrence's exact approved
frozen target. `output_path_recorded` is true only for this durable path
evidence. `output_file_exists` is a point-in-time check of that exact path: it
is true or false when a path is recorded, and null otherwise. Deleting the file
later leaves the recorded path intact and changes only this existence snapshot.

Settled records that predate schema v16 have no trustworthy actual-path
history. They remain `output_path: null`, `output_path_recorded: false`, and
`output_file_exists: null`; migration does not infer a path from a destination
or scan media folders. A current settled record with missing, damaged,
cross-linked, or out-of-scope evidence fails closed with
`503 PLAN_STATUS_UNAVAILABLE` rather than fabricating a path.

The result is scoped to the current Windows user and interactive session.
Unknown and differently owned plan IDs both return 404 PLAN_NOT_FOUND. A
malformed ID returns 400 INVALID_ARGUMENT. Unavailable or inconsistent
persisted status fails closed with 503 PLAN_STATUS_UNAVAILABLE.

### Paged plan execution history

Use the authenticated, read-only history endpoint to inspect every persisted
occurrence for a currently owned standing once, required once, or recurring
daily/weekly plan:

~~~~http
GET /api/v1/plans/{plan_id}/history?limit=20&cursor=<opaque>
X-Agent-Recorder-Key: <api-key>
~~~~

`limit` defaults to 20 and accepts 1–100. Only `limit` and `cursor` are
supported; unknown or duplicate query parameters, invalid limits, and empty,
oversized, or malformed cursors return 400 `INVALID_ARGUMENT`. The response
uses `ok/request_id/data`; `data` contains `plan_id`, `kind`, `items`,
`next_cursor`, and `page_size`. Items are ordered by `window_start_utc`
descending and occurrence ID in SQLite binary ascending order. They include
the persisted occurrence creation/window/status/reason, the optional raw Run
state, required-mode `execution_status_code`, and only validated output-path
evidence. A null `run_id` means no Run was registered. File absence or
inaccessibility changes only the point-in-time `output_file_exists` value; it
does not rewrite the durable lifecycle state. Legacy path-unavailable evidence
remains null.

Each request revalidates ownership using the current Windows SID and
interactive-session binding. Unknown, foreign, and cross-session plans return
404 `PLAN_NOT_FOUND`; unavailable storage or inconsistent owned data returns
503 `PLAN_HISTORY_UNAVAILABLE`. A cursor is bound to its plan and identity and
cannot replace ownership validation. The first page fixes membership at the
maximum SQLite rowid then present; later inserts are excluded
even when their scheduled window is older. The watermark is table-wide while
the cursor remains bound to the requested plan. Ordering uses keyset pagination,
not offset. The watermark freezes page membership only: each page's mutable
status and output evidence reflect that page's own SQLite read snapshot.
Candidate selection and lifecycle/evidence validation are bounded to at most
`limit + 1` candidate rows and `limit` returned rows; the status endpoint's
512-occurrence projection limit does not apply to history.

Capabilities expose `plan_execution_history` only when the current host has
the production owned-plan gateway, with endpoint, supported kinds, and page
bounds. This does not claim global ordinary/plan/future-window history or a
history UI. The Headless host remains unsupported and returns 503.

### Required-mode one-time fixed-region plan

`requested_authorization.mode: "required"` creates a one-time plan without a
Lease. Creation still requires local region selection and a separate local
creation approval. At the scheduled time the tray host rechecks the original
SID/session, display topology and frozen physical region, output readiness,
and latest-start bound, then presents a second local confirmation showing the
target display, region, duration, no-audio setting, and destination. The
creation approval alone never starts capture, and HTTP cannot approve or
trigger the execution confirmation.

The narrow first implementation supports one fixed region, FFmpeg region
capture, no audio, natural wake while the interactive desktop is available,
and a bounded one-time start window. It does not wake the machine or replay a
missed occurrence. Reject, timeout, unavailable desktop, identity/display/
output drift, or corrupt approved specification produces a durable terminal
reason without creating a Run. A committed start that is interrupted by a
process restart is reported as `started_unknown` and is never retried.

Discover the current host capability at `GET /capabilities`:

```json
{
  "required_once_plan": {
    "setup_supported": true,
    "execution_supported": true,
    "authorization_mode": "required",
    "creation_requires_local_approval": true,
    "second_confirmation_required_at_execution": true,
    "due_occurrence_behavior": "local_per_run_confirmation_then_fixed_region_capture"
  }
}
```

`execution_supported` is false until the tray execution runtime has completed
startup recovery, can read the current SID/session's due queue, and has a safe
interactive confirmation host. When false, a scheduled required-mode setup
remains inert and its response reports `execution_runtime_unavailable`.

The durable status endpoint additionally returns `execution_status_code` for
required-mode occurrences. It distinguishes `pending_confirmation`,
`start_committed`, `recording`, `finalizing`, `settled`, `rejected`, `expired`,
`blocked`, `started_unknown`, `session_interrupted`, and `failed`; the existing
occurrence and Run status fields remain the public lifecycle projection.

```json
{
  "ok": true,
  "data": {
    "plan_id": "required-once-plan-...",
    "kind": "once",
    "plan_status": "enabled",
    "schedule_exhausted": null,
    "occurrence_count": 1,
    "latest_occurrence": {
      "occurrence_id": "occurrence-...",
      "window_start_utc": "2026-10-01T01:00:00Z",
      "window_end_utc": "2026-10-01T01:05:00Z",
      "status": "pending_confirmation",
      "terminal_reason_code": null,
      "run_id": null,
      "run": null,
      "output_path": null,
      "output_path_recorded": false,
      "output_file_exists": null,
      "execution_status_code": "pending_confirmation"
    }
  },
  "request_id": "..."
}
```

Only a validated media settlement exposes `output_path` with
`output_path_recorded: true`. Required-mode execution creates neither a
consent Lease nor a Lease-use proof.

## Bounded Daily/Weekly Fixed-Region Plan

The same authenticated `POST /plans` endpoint also accepts a `daily` or
`weekly` recurring schedule when `/capabilities.unattended_lease.recurring`
reports setup and execution support and unattended mode is locally enabled.
Each plan requires a new local region selection and separate recurring lease
approval. The output directory must already exist; it is frozen at approval.

```json
{
  "recording_spec": {
    "source": { "type": "fixed_region" },
    "audio": { "mode": "none" },
    "duration_seconds": 10,
    "countdown_seconds": 0,
    "backend": "ffmpeg-region",
    "output": { "directory": "D:\\Videos", "filename_prefix": "daily-notes" }
  },
  "schedule": {
    "kind": "daily",
    "time_zone_id": "China Standard Time",
    "local_start_date": "2026-10-01",
    "local_end_date": "2026-10-03",
    "local_time": "09:00:00",
    "weekdays": null,
    "maximum_occurrences": 3,
    "latest_start_grace_seconds": 120
  },
  "requested_authorization": {
    "mode": "recurring_lease",
    "valid_until": "2026-10-03T02:00:00Z",
    "max_runs": 3,
    "max_total_duration_seconds": 30
  }
}
```

`weekly` uses a nonempty, unique array of lowercase weekday names instead of
`null`. The local date span is at most 366 days, occurrence count at most 366,
run duration 1-600 seconds, and latest-start grace at most 300 seconds.
`max_runs` must equal `maximum_occurrences`, total duration must cover all
runs, and `valid_until` must be strictly after the latest planned end. Each
due occurrence is revalidated and executed at most once; a missed window or
uncertain start is not retried.

The tray's local unattended safety center lists recurring plans separately
from one-shot authorizations for the current Windows user and session. It
shows the schedule, next local occurrence, remaining lease quota, and active
Run state, and can revoke one exact lease after local confirmation. There is
no HTTP lease-revocation endpoint; an API key or API request is not local
consent.

The recurring `GET /plan-setups/{id}` response reports setup progress and
`scheduled` activation only. Do not interpret `scheduled` as recording started
or completed. Query `GET /api/v1/plans/{plan_id}/status` for the persisted
occurrence/Run outcome and the verified output path when settlement succeeds.

## Lower-Level Endpoints

### Displays

```http
GET /displays
```

Returns the API display selection token, Windows display number when reliably
available, user-facing name, primary flag, scale factor, and virtual-screen
bounds. The API token and Windows number may differ; callers must pass the
returned `id` unchanged when creating a recording.

Example: `id: "display_1"`, `windows_display_number: 2`, and `name: "Display 2"`
is valid. A `null` `windows_display_number` means the agent must request user
clarification using the other metadata instead of guessing.

### Windows

```http
GET /windows?include_minimized=false&include_system_windows=false
GET /windows/active
```

Returns window IDs, titles, process names, active/minimized state, bounds, and
`capture_eligibility_reason_code`. An empty reason means the HWND is a
structurally selectable content surface; otherwise the reason identifies why
the strict window-surface path excludes it (for example, owned auxiliary,
tool, non-activating transient, title-less, zero-area, or off-display). This is
a safe structural diagnostic and does not expose window pixels or additional
window text. Test/injected enumerators may return `not_evaluated`.

The future-window authorization validator applies this same surface rule to
every top-level HWND, then additionally binds the exact approved executable
image, post-approval process creation, current user/session, and candidate-set
uniqueness. The `/windows` endpoint is an inventory and may include ineligible
rows; agents should choose only rows whose eligibility reason is empty. A
multiple-eligible-window result remains an intentional fail-closed ambiguity.
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

Microphone audio may be enabled with `audio.microphone.enabled: true`. Provide `audio.microphone.device_id` from the `input_devices` array of `GET /audio/devices` when multiple active microphones are present; omit it to auto-select a single active device or the single CoreAudio multimedia default. System audio may be enabled with `audio.system_audio.enabled: true`; omit its `device_id` for the current multimedia default or use an exact `output_devices.id`. It cannot be combined with microphone capture and follows the same confirmation, fixed-endpoint, and recovery rules above.

Muted or inactive devices are rejected before any region-selection or confirmation UI is shown (`409 AUDIO_DEVICE_MUTED` and `503 AUDIO_DEVICE_NOT_AVAILABLE` respectively). Agent Recorder never automatically unmutes a device. Unknown state does not block.

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

For the experimental WGC continuous window backend, target loss after capture
starts is also terminal and explicit: `window_closed`, `window_minimized`, or
`size_changed` is preserved as the recording failure reason and shown through
the local failure notification. These are recording terminal reasons rather
than HTTP request error codes.

Both the default FFmpeg and experimental WGC display/region paths fail closed
when the approved target display is lost. For the default FFmpeg display/region
paths, including the split A/V variants, the engine also supervises the approved
display binding after the first credible frame. It re-enumerates display metadata
and requires exactly one
resolved match for the approved internal stable display identity. Public
`display_N` ordinals, names, bounds, and backend-reported text are not used as
runtime identity. If the target is missing, ambiguous, unresolved, replaced,
or the topology provider fails, the engine takes an application-owned typed
abort and publishes the terminal reason `display_unavailable`. This is not a
user stop: the final MP4 and bundle are not published, and the local tray host
receives one failure notification. A/V split abort stops both workers and skips
the mux step; any partial worker artifacts remain subject to the controlled
failed-artifact retention policy. A later request can bind again after the
display topology is restored.

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
  "output": {
    "path": "...",
    "duration_seconds": 300.0,
    "encoder_mode": "hardware",
    "encoder_selection_reason": "hardware_selected"
  },
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
- Returns `0` if capture has not actually started (e.g. `created`, `pending_confirmation`, `preparing`, `countdown`, `rejected`, `expired`).
- During `preparing` and `countdown` the value stays `0`; these phases are not part of the recorded screen-capture duration.
- For active recordings (`recording`, `stopping`) it grows with each query.
- When capture ends the state switches to `finalizing` and `elapsed_seconds` freezes at the actual screen-capture duration; finalization time (mux, ffprobe, bundle) is not added.
- For terminal recordings it is computed from `completed_at` and remains stable across repeated queries.
- `output.duration_seconds` is the media file duration from ffprobe; the two may differ slightly due to encoding, rounding, and backend behavior. Do not use `duration_seconds` as a substitute for `elapsed_seconds`.
- `output.path` is always the locally user-approved final save target (`Recording.OutputPath`). On success it points to the published final media; on failure the final media is not published and `bytes_written`/`size_bytes` is `0`. A/V split worker files, mux staging files, and failed diagnostic retention files are internal evidence and are never public output paths.

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
| `output.encoder_mode` | Actual WGC encoder mode: `software` or `hardware`. Empty for backends that do not report this evidence. |
| `output.encoder_selection_reason` | Stable WGC selection evidence: `software_default`, `hardware_selected`, `hardware_unavailable_fallback`, `hardware_init_failed_fallback`, or `hardware_unverified_fallback`. Empty for other backends. |

`hardware_selected` is reported only after the H.264 transform actually used by
the Media Foundation Sink Writer is verified as a hardware MFT. Hardware
availability probes and request preferences alone are not treated as proof.

For microphone recordings, terminal responses include `audio.microphone` diagnostics:

| Field | Description |
| --- | --- |
| `status` / `continuity_status` | Final audio presence and continuity result. |
| `capture_strategy` | Selected helper path, such as `hfp-duplex-prime-classic` or ordinary direct capture. |
| `pair_evidence` | Evidence used to pair capture/render endpoints, such as `same_container_id`. |
| `auto_hfp_pair_status` / `auto_hfp_pair_result_code` | Automatic HFP discovery outcome. |
| `auto_hfp_pair_transport_classification` | Passive transport classification (`hfp_candidate`, `not_hfp`, or an unavailable/unknown result). |
| `render_prime_ready_ms` | Time required to establish the paired render stream, when applicable. |
| `helper_failure_reason` / `helper_failure_stage` / `helper_failure_hresult` | Stable helper failure evidence; empty on success. |

Detailed current/max gap, recovery, gap-fill, and discontinuity counters are written to the local audit event for the terminal recording. `EstimatedGapMs` is a current gauge and may decrease; `MaxEstimatedGapMs` is the monotonic historical maximum.

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
| `preparing` | Capture-safe barrier done; preparing microphone (no REC, no screen capture) |
| `countdown` | 3-2-1 countdown is shown (no REC, no screen capture) |
| `recording` | Screen capture is active |
| `finalizing` | Screen capture ended; cropping, muxing, probing and bundle generation in progress |
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

## Chapter Marks

Add a labeled mark to one active recording:

```http
POST /recordings/{recording_id}/marks
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "label": "Important decision"
}
```

`source` may be omitted and defaults to `agent`; an authenticated API caller
may explicitly send only `agent`. The reserved `hotkey` source is assigned by
the local `Ctrl+Shift+F11` path and cannot be forged remotely.

Success returns the accepted mark without requiring another GET:

```json
{
  "ok": true,
  "data": {
    "recording_id": "rec_xxx",
    "mark": {
      "t_ms": 1234,
      "label": "Important decision",
      "source": "agent"
    }
  },
  "request_id": "req_xxx"
}
```

The endpoint accepts marks only in the exact `recording` state. `label` is
trimmed after Unicode/control-character validation, must remain non-empty, and
may contain at most 200 Unicode scalar values. `t_ms` is a non-negative integer
on the recording's trusted first-frame monotonic timeline.

| HTTP | Code | Meaning |
| ---: | --- | --- |
| 400 | `INVALID_ARGUMENT` | Invalid JSON, label, or source. |
| 404 | `RECORDING_NOT_FOUND` | Recording ID does not exist. |
| 409 | `RECORDING_NOT_ACTIVE` | Recording is not in the exact `recording` state or has no trusted first-frame anchor. |

## Recording Bundle

Ordinary locally confirmed video with exact backend `wgc-continuous` (silent display, region or strict window) or `wgc-window-av-split` (strict window with fixed system loopback) also uses this same five-file Bundle and durable generation digest chain after successful MP4 publication. Existing raw/quick/Profile/nested request rules, backend eligibility, experiments and audio support remain in force; this does not expand independent standing/recurring/required/future execution. Bundle generation remains asynchronous and its failure leaves the completed MP4 intact. History GET does not hash or backfill content; explicit verification uses existing endpoints and budgets. Native evidence unavailable means the existing compatible unknown result. Source acceptance covers synthetic inputs with the production session/publication pipeline; dedicated native-capture acceptance remains pending. The public v0.1.15 package predates this increment.

Successful supported FFmpeg and ordinary WGC MP4 recordings produce a structured bundle next
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
| `not_applicable` | Recording failed, used a WGC backend, or no bundle generator is enabled. |

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
    "audio_microphone": true,
    "audio_capture_backend": "wasapi-helper",
    "audio_status": "recorded",
    "audio_device_id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}",
    "audio_lost_at_ms": null,
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

`recording.audio_status` values:

| value | meaning |
| --- | --- |
| `not_requested` | No microphone audio was requested. |
| `recorded` | Microphone audio was successfully written to the output and an AAC stream is present. |
| `start_failed` | Microphone was requested but FFmpeg could not open the device. |
| `lost` | The microphone device was lost or disconnected during recording, but an AAC stream is still present. |
| `missing_audio_track` | Microphone was requested and FFmpeg exited cleanly, but the output does not contain an AAC audio stream. |

`recording.audio_continuity_status` values (new):

| value | meaning |
| --- | --- |
| `not_checked` | Continuity was not checked (e.g. no microphone, or finalization did not run). |
| `continuous` | The final AAC track was checked and no internal long silence was detected. |
| `degraded` | The final AAC track contains an internal silence gap that may indicate a microphone signal interruption. The `warnings` array includes `microphone_signal_interruption_suspected`. |

`audio_status` remains available for backward compatibility. A value of `recorded` no longer implies that the audio is free of internal gaps; agents that care about continuity should also inspect `audio_continuity_status`.

### `marks.json`

The versioned marks file contains every accepted chapter mark in insertion
order. A recording with no marks keeps an empty array.

```json
{
  "bundle_version": 1,
  "recording_id": "rec_xxx",
  "marks": [
    { "t_ms": 1234, "label": "Important decision", "source": "agent" }
  ]
}
```

Bundle generation reads an immutable snapshot of the recording's marks. The
`recording.mark_added` audit event includes recording ID, timestamp, and source,
but deliberately excludes the label text.

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
| `AUDIO_DEVICE_MUTED` | Selected microphone is muted; unmute it in Windows sound settings and retry |
| `AUDIO_DEVICE_NOT_AVAILABLE` | Selected microphone is inactive or no microphone is available; check the device and retry |
| `AUDIO_DEVICE_NOT_FOUND` | The requested `audio.microphone.device_id` was not found; list devices and retry |
| `AUDIO_DEVICE_REQUIRED` | Multiple microphones are available and `audio.microphone.device_id` is required |
| `METHOD_NOT_ALLOWED` | HTTP confirmation approval/rejection is blocked |

## Window capture semantics

The pending confirmation summary contains privacy-safe planning fields:
`capture_semantics`, `planned_backend`, `preview_semantics`, `window_id`,
`selection_reason_code`, and `selection_fallback`. These fields describe the
approved plan; they do not expose helper paths, probe output, tokens, or native
handles beyond the existing `window_id` contract.

`window_surface` means the selected HWND content and excludes unrelated windows
covering it. The strict request above uses this class. `screen_rectangle` means
the composed desktop rectangle used by legacy `ffmpeg-window-region` or its A/V
split variant; a covering window may therefore appear. Omitting
`required_capture_semantics` preserves that legacy choice. Display and region
requests use the explicit `display_surface` and `region_rectangle` classes.

The plan is built before confirmation without constructing or starting a backend.
After local approval it is revalidated without pixel capture. A window identity or
semantic change returns a terminal `capture_semantics_changed` failure before
countdown, authorization, helper/FFmpeg start, recording controls, or output
creation. The caller must issue a new request. The separate display/region WGC
experiment remains experimental and default-off.

## Configurable pre-capture countdown

`POST /api/v1/recordings` and `POST /api/v1/recordings/quick` accept the same
optional top-level `countdown_seconds` field. It defaults to `3` and accepts
only integer values from `0` through `10`. Negative values, values above `10`,
fractions, strings, booleans, `null`, objects, and arrays return
`400 INVALID_ARGUMENT` before quick target/source resolution or region-selection
UI. A value of `0` skips visible digits and countdown audit events, but does
not skip confirmation, preparation, preflight, capture authorization, or
credible-first-frame gating. The normalized value is returned in the
confirmation summary and recording status `config.countdown_seconds` field.
`/capabilities.interaction.countdown` reports the range, default, and
`capture_during_countdown=false`; quick recipe templates include the default.

## Bounded screenshot series

The raw and quick recording endpoints also accept `mode: "screenshot_series"`.
The canonical fields are `interval_ms` (`1000..3600000`) and exactly one of
`max_count` (`1..300`) or `max_duration_seconds` (`1..86400`). Duration mode
plans `ceil(max_duration_seconds * 1000 / interval_ms)` captures and rejects a
plan above 300 frames. Audio is not part of this mode and is rejected before
source or device resolution with `400 INVALID_ARGUMENT`. Raw `stop_condition`, or
quick `duration_seconds`/`stop_condition`, is also rejected there with a stable
field and suggested action; use `max_duration_seconds` for a series duration.

The public response adds `mode`, `series`, and a PNG-sequence `output`. A series
uses one single-frame FFmpeg process per anchored schedule point. The final
directory contains numbered PNG files and `series.json`; normal completion and
partial cancellation with frames publish the directory, while zero-frame
cancellation or failure leaves no final directory. Duration starts at the first
valid PNG atomic commit (`t=0`); reaching the deadline before the next start claim
is normal `completed` and may leave planned and captured counts different. Chapter
marks and the MP4 bundle are not applicable. Bounds are explicitly
`virtual_screen` and the approved plan, runner request, confirmation summary, and
manifest carry the same coordinate-space value.
Each scheduled point claims its start serially and launches one bounded FFmpeg
single-frame process; the worker never launches parallel catch-up frames. The
first valid PNG atomic submission is the `t=0` anchor. In `series.json`,
`captured_offset_ms` is the valid-submit offset, `lateness_ms` is non-negative
claim lateness excluding the frame's own capture/encode time, and
`capture_duration_ms` is the monotonic claim-to-valid-submit duration. These
are truthful diagnostics, not a fixed desktop-latency or real-time guarantee.

## Future-window one-shot authorization

Capability discovery is under `/capabilities.future_window_one_shot`. Both
`setup_supported` and `execution_supported` must be true before an agent offers
this flow. All routes require `X-Agent-Recorder-Key`.

Create a pending scope with `POST /api/v1/future-window-authorizations` and an
`Idempotency-Key` header:

```json
{
  "executable_path": "C:\\Program Files\\Player\\player.exe",
  "audio": { "mode": "none" },
  "maximum_duration_seconds": 300,
  "validity_seconds": 900,
  "output_directory": "D:\\Recordings"
}
```

`audio` must explicitly be `none` or `system_loopback` with one exact render
`endpoint_id`. Limits are 1–1800 seconds per run and 1–3600 seconds of grant
validity, and the requested maximum run duration must not exceed validity.
After local approval, the authorization end is `expires_at_utc`; the latest
permissible start is `latest_permissible_start_at_utc`, computed as expiry
minus the complete maximum run duration. Equality is allowed only when the
entire run ends at the authorization boundary. The server verifies and freezes
the local executable identity and output target. The 202 response includes
`data.result` (`created`/`existing`)
and `data.authorization` (initially `pending`). Only explicit local desktop
approval activates the grant. An idempotent retry returns the original setup;
reusing its key for a different normalized scope returns 409.

Read with `GET /api/v1/future-window-authorizations/{authorization_id}`. After
local approval, the agent may request the sole run with
`POST /api/v1/future-window-authorizations/{authorization_id}/runs` and
`{"window_id":"window_123456"}`. That ID must be obtained locally from the
window inventory; the Recorder verifies the unique visible, non-minimized
top-level window and its post-approval process/executable identity. No client
PID/hash claims, title matching, alternate window, rectangle fallback, or
automatic retry is accepted. A successful 202 response provides `run_id` and
`status_url`; poll the recording route and the authorization GET for terminal
output evidence. A crash after the atomic consume may leave
`run_status=started_unknown`, which must not be retried.

Users can inspect/revoke grants in the local safety control center; an
authenticated agent may also call
`POST /api/v1/future-window-authorizations/{authorization_id}/revoke` with an
empty body or `{}`. Revocation blocks any new start and requests stop of the
bound active recording without reporting a normal duration completion. System
loopback captures all sound sent to the selected render endpoint, not only
sound from the selected program window.
The local Stop All action revokes all pending, active, or consumed future-window
grants for the current Windows SID/session. Disabling unattended mode also
revokes those grants and requests a stop for an active future-window run;
reenabling never restores them. Both actions are enforced again at the final
backend gate. A grant that cannot cover its full approved run before expiry is
rejected, never duration-truncated.
This API does not launch players, recognize livestreams, inspect browser tabs,
or provide recurring schedules, wake, microphone capture, or fallback capture
semantics.

## Fixed-region profile management

The authenticated `/api/v1/profiles` routes manage reusable configuration only.
This slice supports fixed-region, `exact_match_only`, `ffmpeg-region`, and no
audio. Copying an exact immutable version does not select new geometry or
validate current display availability. Fresh creation uses the local-selection
reference described below. Management alone cannot authorize capture or attach
a profile to a plan; ordinary recording reuse has its own local-confirmation
contract below.
Saving a profile never opens a confirmation dialog, starts a worker, issues a
proof, reserves lease quota, creates an output directory, or creates media.

Create by copying an exact version:

```http
POST /api/v1/profiles
X-Agent-Recorder-Key: <key>
Idempotency-Key: <stable-client-key>
Content-Type: application/json

{
  "name": "Lesson capture",
  "source_profile_ref": {
    "id": "profile-id-from-list",
    "version": 1,
    "digest": "recurring-fixed-region-profile/v1:<64 lowercase hex characters>"
  },
  "changes": {
    "duration_seconds": 30,
    "countdown_seconds": 3,
    "output_directory": "D:\\AgentRecorder\\.local-data\\Videos",
    "filename_prefix": "lesson"
  }
}
```

`name` is required (1–80 readable characters). `source_profile_ref` requires
exactly `id`, positive `version`, and `digest`. `changes` is optional; its only
fields are `duration_seconds` (1–600), `countdown_seconds` (0–60),
`output_directory`, and `filename_prefix`. Omitted values copy from that exact
version. Unknown or duplicate JSON properties, non-integer numbers, traversal
segments, invalid prefixes, and authorization/selection/audio/target fields are
rejected. A missing source returns 404; no default full-screen profile is
created. The server assigns a new profile ID and version 1.

`GET /api/v1/profiles` is keyset-paginated in stable profile-ID order (default
20, maximum 100); use its opaque `next_cursor` as `cursor`. Legacy immutable
profiles are included with a deterministic fallback name. Deleted profiles are
hidden unless `include_deleted=true`. Detail returns the name, exact current
`profile_ref`, specification, deletion state, and strong ETag. Use
`GET /api/v1/profiles/{profile_id}/versions?limit=...&cursor=...` to page
immutable versions, and `/versions/{version}` to read one exact version.
Cursors are opaque, bounded, and scoped to their resource.

`PATCH /api/v1/profiles/{profile_id}` accepts a non-empty JSON object containing
only `name` and/or the four `changes` fields above. Every accepted PATCH creates
one new immutable version; prior version digests and existing plan bindings do
not change. `DELETE /api/v1/profiles/{profile_id}` is a logical tombstone and
preserves all versions. Both require exactly one current strong `If-Match`
header copied from the ETag response header; missing is 428, stale or
cross-profile is 412, and weak, wildcard, or multiple values are 400. A
successful mutation returns the new ETag both as the HTTP `ETag` header and in
the JSON data envelope. Concurrent same-tag PATCH requests have one winner;
the other must GET before trying again.

Delete checks references to **any** historical version in the same SQLite write
transaction as the tombstone. Referenced profiles return 409 `PROFILE_IN_USE`
with a bounded plan-reference summary; no plan or authorization is changed.
The initial plan-binding transaction also rejects a tombstoned profile, while
an exact existing binding replay remains readable. A tombstone blocks future
edit, copy, and initial binding; historical reads remain available.

POST requires `Idempotency-Key`. The key is atomically bound to the normalized
method, path, and request. An exact retry—including after process restart—returns
the original version-1 result and ETag; reuse with a different request returns
409. The immutable version, directory entry, and idempotency result commit in
one transaction. `/capabilities.profile_management` lists `list`, `get`,
`list_versions`, `get_version`, `copy_existing_version`, `patch`, and `delete`,
and conditionally `create_from_local_selection`;
it explicitly reports `supported_targets=[fixed_region]`; interactive selector
hosts also report `create_from_local_selection` in operations and
`creation_modes`. Headless hosts advertise copy only. The compatibility
field `recording_or_plan_profile_ref_supported=false` remains false because
Plan profile refs are unsupported; the split fields report
`ordinary_recording_profile_ref_supported` and
`ordinary_recording_execution_supported` separately from
`plan_profile_ref_supported=false`. Management routes are not an approval
channel.

#### Create the first profile from a local selection

On an interactive host, `POST /api/v1/regions/select` with
`{"purpose":"profile"}` opens the existing local region selector. The older
`POST /api/v1/region-selections` route remains available for ordinary recording
selection and keeps its established behavior. A successful profile-purpose
selection returns the exact virtual-screen bounds plus an opaque
`selection_ref` and `expires_at`; the reference is process-local and valid for
five minutes. Cancellation, timeout, ordinary recording selection, and saved
last-region state do not produce a usable profile reference. Profile selection
does not approve or start a recording.

Create a named version 1 with the returned reference:

```http
POST /api/v1/profiles
Idempotency-Key: <new stable key>
Content-Type: application/json

{"name":"Lesson","source_selection_ref":"sel_<opaque>","changes":{"duration_seconds":30,"countdown_seconds":3,"filename_prefix":"lesson"}}
```

`changes.duration_seconds` is required (1–600); countdown defaults to 3 and is
limited to 0–10; filename prefix defaults to `recording`; omitted output
directory freezes the effective configured default at creation. The profile
request rejects stale references, changed/incomplete display topology, odd or
small geometry, and selections crossing display bounds; it never clips or
rewrites the geometry. Exact retries return the original immutable creation
snapshot before checking the ephemeral reference, including after expiry or
restart. Conflicting reuse of an idempotency key returns 409. Profile creation
does not create the output directory, probe/write output, start capture, or
issue recording authorization. Headless hosts do not advertise interactive
selection creation in `creation_modes`.

### Interactive recording from a fixed-region profile

`POST /api/v1/recordings` also accepts the exclusive body
`{"profile_ref":{"id":"...","version":1,"digest":"..."}}`.
All fields are required and bind one immutable version; names, `latest`, raw
capture overrides, Plan/Lease/proof fields, unknown fields, and duplicate JSON
properties are rejected. The authenticated request reads the exact version and
directory tombstone from one SQLite snapshot. A deleted profile, missing
version, digest mismatch, unavailable gateway, or changed display environment
returns an explicit error and never falls back to raw/default capture settings.

Only fixed-region, exact-match, `ffmpeg-region`, no-audio profiles are accepted.
Duration must be a whole 1–600 seconds; incompatible sub-second values and
odd/small geometry are rejected without rounding or rewriting the profile.
The profile's absolute output directory, filename prefix, and `fail_if_exists`
policy apply to an ordinary run filename containing the recording ID. Local
confirmation remains mandatory for every run. The user may change the output
directory for that run; the effective path is used without writing back to the
profile. Profile configuration is not capture authorization. This POST has no
idempotency contract and must not be blindly retried after an ambiguous
response. Plan profile references remain unsupported.

An environment rejection found while processing the request is returned
synchronously in the HTTP response. Once the request has been accepted with
HTTP 200 and is awaiting local confirmation, a later environment change does
not retroactively change that response to HTTP 409. The recording instead
settles as failed with `stop_reason` and `error` set to
`PROFILE_ENVIRONMENT_CHANGED`; query the recording or its status-wait endpoint
to observe the terminal result.

Profile requests use the standard error envelope with `details.field` and
`details.reason_code`: malformed/mixed bodies return 400
`INVALID_PROFILE_RECORDING_REQUEST`; a missing exact version returns 404
`PROFILE_VERSION_NOT_FOUND`; digest mismatch, tombstone, environment drift, or
backend mismatch return 409 (`PROFILE_REF_MISMATCH`, `PROFILE_DELETED`,
`PROFILE_ENVIRONMENT_CHANGED`, or `PROFILE_BACKEND_UNAVAILABLE`); unsupported
duration/geometry/policy returns 422 `PROFILE_NOT_EXECUTABLE`; unavailable
snapshot infrastructure returns 503 `PROFILE_EXECUTION_UNAVAILABLE`.

The directory list, version page, and exact-version response each derive
directory name/deletion state, current profile_ref, is_current, and response
ETag from one SQLite read snapshot. Read specifications use canonical policy
codes such as physical_virtual_screen, landscape_flipped, fail_if_exists,
natural_wake_only, and interactive_desktop_required. Duration reads preserve
precision: duration_seconds is an exact JSON number (integer for whole seconds,
fractional for sub-second values), and duration_ms is the exact integer
millisecond value. Whole-second responses remain integer-valued. POST/PATCH
inputs still accept only integer duration_seconds from 1 through 600.

## Durable ordinary recording history

```http
GET /api/v1/recording-history?limit=20&cursor=<opaque>
X-Agent-Recorder-Key: <api-key>
GET /api/v1/recording-history/{recording_id}
X-Agent-Recorder-Key: <api-key>
```

This read-only API returns durable **ordinary video recording** history,
including raw/quick requests, ordinary nested recordings and exact fixed-region
profile references. It excludes automatic, standing/recurring/required-once/
future-window executions and screenshot series. `limit` defaults to 20 and must
be 1–100. The opaque bounded cursor uses stable keyset ordering and a first-page
high-water mark; entries created while paging are excluded from later pages.
Each page and detail is read from one SQLite snapshot. List responses do not
probe media. Detail adds best-effort `FileInfo` metadata (existence, size and
last-write time) for the recorded main path, without reading contents or checking
original identity. Use the explicit artifact check for bounded, handle-based
media/attachment observations. A missing detail ID
returns the standard 404 error envelope. All endpoints require
`X-Agent-Recorder-Key`; capabilities describe this scope under
`recording_history`.

An accepted ordinary recording is synchronously registered before the create
request returns or capture can start. A registration failure returns 503 and
fails closed. Subsequent real lifecycle state changes are queued to one bounded
SQLite writer; terminal states are monotonic, and active rows left by process
termination are reconciled to `interrupted_unknown` on the next startup. This
history is a separate projection and does not create or mutate plan, lease, run,
proof, or authorization records.

```json
{
  "ok": true,
  "data": {
    "scope": "ordinary_video",
    "items": [{
      "recording_id": "rec_123",
      "status": "recording",
      "source_type": "display",
      "capture_semantics": "display_capture",
      "audio": { "mode": "system_loopback" },
      "output_path": "C:\\Recordings\\capture.mp4",
      "nested": null,
      "profile_ref": null
    }],
    "next_cursor": null,
    "page_size": 20
  },
  "request_id": "req_123"
}
```

## Unified read-only task history

This contract is implemented in the current source tree; it is not a released
package announcement. Inspect `capabilities.unified_history` before using it.

The current source persists successful future-window MP4 completion and the original private publisher identity with the exact authorization/run and frozen output in the same SQLite transaction (append-only v40). Native identity unavailable at publication remains explicitly unknown; pre-v40 rows remain legacy unknown without scanning or backfilling media. Unified history validates selected private facts in one bounded indexed batch within its existing SQLite snapshot. Missing modern markers or inconsistent bindings return the existing unavailable response. No public fields, endpoints, capabilities, Bundle delivery, content verification, or authorization scope are added. This records historical publication identity; it does not verify current media bytes or announce a released portable package.

App supports `ordinary`, `plan`, and `future_window`. Headless advertises only
`["ordinary"]` and does not start Plan or future-window runtimes. `source=all`
means all sources advertised by that host. An explicit unsupported source
returns 400 `INVALID_ARGUMENT` with a host-support explanation.

```http
GET /api/v1/history?limit=20&source=all&created_from=2026-10-01T00:00:00Z&created_before=2026-11-01T00:00:00Z
X-Agent-Recorder-Key: <api-key>
```

Only `limit`, `source`, `status`, `profile_id`, `profile_version`, `source_type`, `plan_id`, `target_key`, `created_from`, `created_before`, and `cursor` are accepted;
unknown or duplicate query parameters are invalid. `limit` defaults to 20 and
must be an integer from 1 to 100. Sources are exact lower-case codes. Times must
include seconds and explicit UTC (`Z` or zero offset); percent-encode `+` in an
HTTP query. The lower bound is inclusive, the upper exclusive, and a supplied
interval must have `created_from < created_before`. Omitted bounds are unbounded.

Optional `status` requires explicit `source=ordinary`. It accepts exactly one
lower-case persistent code: `pending_confirmation`, `preparing`, `countdown`,
`recording`, `finalizing`, `completed`, `failed`, `cancelled`, `rejected`, `expired`,
or `interrupted_unknown`. Empty, uppercase, comma-separated, unknown or duplicate
values (including URL-encoded duplicate keys) return 400 `INVALID_ARGUMENT`.
Omitting status preserves all existing source behavior. Status is applied in the
indexed database query before pagination and can combine with creation bounds.

```http
GET /api/v1/history?source=ordinary&status=failed&limit=20
X-Agent-Recorder-Key: <api-key>
```

`capabilities.unified_history.status_supported_sources` is `["ordinary"]` and
`supported_statuses` lists these codes when an ordinary history gateway is
available; both lists are empty when history is unconfigured.


Optional `profile_id` and `profile_version` select persisted ordinary Profile associations.
Either parameter requires explicit `source=ordinary`, including on Headless;
omitted source or `all`/`plan`/`future_window` with these parameters returns 400.
ID matching is exact and case-sensitive, independent of display name, current
version, rename, or logical deletion. IDs use existing Profile validation (1–128
characters, no surrounding whitespace, control characters, slash, backslash, or
colon), without trimming or case folding. `profile_version` requires `profile_id`
and unsigned decimal positive Int64 syntax (no sign, whitespace, fractions,
exponents, or overflow). ID-only matches all persisted versions; ID plus version
matches that exact version. Unknown valid IDs/versions return an empty page,
without consulting the Profile directory or requiring a live configuration.

```http
GET /api/v1/history?source=ordinary&profile_id=prof_example&limit=20
X-Agent-Recorder-Key: <api-key>
```

```http
GET /api/v1/history?source=ordinary&profile_id=prof_example&profile_version=2&status=completed&limit=20
X-Agent-Recorder-Key: <api-key>
```

Both conditions combine with status and creation bounds and apply in parameterized
SQL before LIMIT. Additive v26 only adds two ordered indexes for ID-only and exact
version seeks; historical rows and previous migration definitions remain unchanged.
`capabilities.unified_history.filters` includes both parameters;
`profile_supported_sources` is `["ordinary"]` only with an ordinary gateway,
otherwise empty. `profile_version_requires_profile_id=true` and
`profile_match=exact_persisted_id_and_optional_version` describe the matching rule.
The default output directory remains `.local-data/Videos`.
No additional UI, capture, authorization, or retention filter is introduced.

Optional `source_type` requires explicit `source=ordinary` and matches the persisted
recording category exactly: `display`, `window`, `region`, or `fixed_region_profile`.
Profile recordings persist `fixed_region_profile`; it is distinct from `region`.
This category does not identify a particular window or display. No historical rows
are rewritten; unfiltered requests still return older allowed source types.
Empty, uppercase, list, unknown, or incompatible-source values return 400.
It can combine with status, Profile association, creation time and pagination;
a valid intersection with no matches returns an empty page.

```http
GET /api/v1/history?source=ordinary&source_type=window&created_from=2026-10-01T00:00:00Z&limit=20
X-Agent-Recorder-Key: <key>
```

Optional `plan_id` requires explicit `source=plan`. It is a Plan ID, with the
same canonical 1–128 character ASCII letters/digits/`-_.` boundary as dedicated
Plan history: exact case-sensitive equality, no trimming or fuzzy match. It can
combine with creation time and pagination, but not ordinary status, Profile or
source-type filters. Empty, invalid and duplicate parameters return 400.
Unknown valid IDs and Plans owned by another server SID/session return success
with an empty page; their evidence is not read. Headless rejects plan source.

```http
GET /api/v1/history?source=plan&plan_id=plan_example&created_from=2026-10-01T00:00:00Z&created_before=2026-11-01T00:00:00Z&limit=20
X-Agent-Recorder-Key: <key>
```

Both predicates run in SQL before LIMIT. v27 only adds two ordered indexes;
status/Profile intersections may require residual scanning even though candidates
and evidence projections remain bounded. The locator Plan filter retains original
owner/occurrence checks. Unified Plan history sorts by creation time; dedicated
`/plans/{id}/history` sorts by window start, and the two are not interchangeable.
The default output directory remains `.local-data/Videos`.
`capabilities.unified_history.filters` includes `source_type` and `plan_id`.
`source_type_supported_sources=["ordinary"]` and `supported_source_types` lists
the four codes when an ordinary gateway exists; otherwise both lists are empty.
`plan_id_supported_sources=["plan"]` only when plan is supported, empty for
Headless or missing gateway. Match descriptions are
`source_type_match=exact_persisted_recording_category` and
`plan_id_match=exact_case_sensitive_plan_id_with_current_server_ownership`.
Discovery performs no capture, migration, device probe or UI operation.
Retention preview continues rejecting both new parameters.

Members are accepted durable ordinary video recordings in the host's ordinary
history directory, server SID/session-owned Plan occurrences from standing-once,
recurring local approval, or required-once setup, and each owned future-window
authorization starting at authorization creation. Unused pending/active/expired/
revoked authorizations are tasks, even without a Run. A later Run remains on the
same authorization entry. This is a task-creation timeline, not a successful
media gallery, last-update timeline, or aggregate of all users' histories.
Screenshot series are excluded.

Optional `target_key` requires explicit `source=ordinary`. It is an exact frozen
target fact key: `target:v1:display:<64 lowercase SHA256 hex>` or
`target:v1:region:<64 lowercase SHA256 hex>` or
`target:v1:window:<64 lowercase SHA256 hex>` (82, 81 or 81 characters respectively).
Empty, uppercase, malformed, duplicate or incompatible source/Plan combinations
return 400 `INVALID_ARGUMENT`; an unknown canonical key returns an empty page.
It combines with creation bounds, ordinary status, source_type and Profile/version.
The indexed SQL applies it before sorting and LIMIT; each page validates at most
100 target facts in the same SQLite snapshot as media and Bundle evidence.

Ordinary list/detail and unified ordinary items expose nullable `target_key`.
The key freezes once from the immutable accepted CapturePlan before first history
registration: underlying source kind, capture semantics, coordinate space,
explicitly resolved stable display identity, display bounds and actual normalized
pixel bounds. A manual region and an equivalent fixed_region_profile share a
region key. Backend, title, duration, audio, output and Profile version are excluded.
Screenshot series, legacy window screen_rectangle, nested window requests, other
history sources, old rows and unsupported or untrusted facts remain null. No old-row
backfill supplies a key. Strict ordinary window_surface video freezes HWND bits, PID,
process creation UTC FILETIME, Windows session ID, semantics and approved surface
width/height from the accepted plan and one read-only native metadata observation
before durable registration. No pixels, executable paths or signatures are read.
Missing, exited or changed metadata yields null without disabling a legal recording.
Title, window position, audio, output and duration do not affect the key. The value
is a frozen-fact retrieval key, not a permanent Windows window UUID: the same process
may destroy and reuse an HWND with the same facts. It cannot authorize capture,
identity revalidation, retention or deletion. Later updates and queries reuse the
frozen value, even after the window closes; they never observe the desktop again.
It is neither an authorization nor a cross-computer permanent ID or tamper-proof
physical identity claim. Target corruption returns history unavailable (503);
retention keeps its existing protections and fails before file probes.

`capabilities.unified_history` advertises `target_key` in filters,
`target_key_supported_sources=["ordinary"]` when ordinary is configured,
`target_key_source_types=["display","region","fixed_region_profile","window"]`,
`target_key_match="exact_frozen_screen_target_v1"`, and
`target_key_window_supported=true` with ordinary configured,
`target_key_window_semantics="window_surface"` and
`target_key_window_match="exact_frozen_window_facts_v1"`. Source/type lists are empty without a gateway.

```http
GET /api/v1/history?source=ordinary&target_key=target:v1:region:aacff74f6dc6d9532b84651fad18a7bfd7e52827837b84ba52e3437c5b78e465&limit=20
# Use the window key returned by ordinary history; retain all filters with next_cursor.
GET /api/v1/history?source=ordinary&source_type=window&target_key=target:v1:window:<64-lowercase-hex-from-history>&limit=20
X-Agent-Recorder-Key: <api-key>
```

Ordering is immutable `created_at_utc DESC`, then source BINARY ASC, then the
source-local identifier BINARY ASC. `entry_id` prefixes the local ID with its
source (`ordinary:`, `plan:`, or `future_window:`). A first page freezes insert
watermarks for its advertised sources. Subsequent pages exclude later inserts,
even backdated ones, but read current states and evidence from one new SQLite
snapshot per page. The opaque cursor is bounded to 2048 characters and binds
schema/query version, server identity and host scope, and normalized filters.
Keep the same filters, including nullable status, profile_id, profile_version, source_type, plan_id, and target_key; changing status requires a new
first page, otherwise the cursor returns 400. Page size may change. Reopening the store with the same server SID/session against the same valid
migrated store does not invalidate the cursor. Existing cursors with omitted/null
status and omitted/null profile fields remain valid after additive v24/v26 index migrations
for the same unfiltered-by-profile query. They cannot be used with profile filters;
switching ID, version, or ID-only/exact version requires a new first page.
After v27, old cursors with omitted/null sourceType/planId (including pre-status
and pre-Profile cursors) remain valid for the same old query without resetting
watermarks. They cannot be reused with a new type/Plan filter; changing either
requires a new first page. Do not parse or construct cursors.
After v32, cursors with omitted/null targetKey remain compatible with the same old
query. A target cursor cannot change or remove target_key; start a new first page.
Status matches use each page's current snapshot. A state change may remove an
unread match or introduce one before the cursor position, which cannot be
revisited by continuing forward. Refresh the first page to rebuild the filter;
a dynamic status traversal does not promise exactly-once membership.

The standard success envelope contains `data.items`, `next_cursor` (null at the
end), requested `page_size`, and normalized `filters` (`source`, `created_from`,
`created_before`, `status`, `profile_id`, `profile_version`, `source_type`, `plan_id`, `target_key`, null for omitted values). Items use this explicit whitelist:

| Fields | Meaning |
| --- | --- |
| `entry_id`, `source`, `entry_kind`, `created_at_utc` | Stable task identity and creation time; kinds are `ordinary_video`, `plan_occurrence`, `future_window_authorization` |
| `recording_id`, `plan_id`, `occurrence_id`, `authorization_id`, `run_id` | Relevant IDs; inapplicable or absent values are null |
| `source_type` | Persisted ordinary category; null for other sources; not a target identity |
| `target_key` | Frozen ordinary display/region pixel target or strict window facts; null for unknown, unsupported, old or other sources |
| `profile_id`, `profile_version` | Immutable recorded ordinary Profile association; null for unassociated ordinary and all other sources. No digest or configuration is exposed |
| `status`, `reason_code` | Persistent source state and reason |
| `authorization_status`, `occurrence_status`, `run_status`, `run_reason_code`, `execution_status_code` | Separate source-specific states; inapplicable values are null |
| `output_path`, `output_path_recorded`, `output_file_exists` | Valid recorded output evidence versus present/accessibly existing file; absent evidence gives null path, false recorded, null existence |
| `output_size_bytes`, `output_duration_seconds` | Available validated persistent metadata; otherwise null |
| `started_at_utc`, `completed_at_utc`, `window_start_utc`, `window_end_utc` | Available source facts only; no invented Run times |

Ordinary successful active stops use their real completed publication and media
metadata; completion is not restricted to the duration-reached reason. Plan
rows reuse the existing ownership, lifecycle, receipt/settlement and
output-path checks. Legacy settled Runs without recorded paths stay completed
with `output_path_recorded=false`. Future pending/active/used or unsuccessful
entries never expose planned paths as completed media. A missing file does not
rewrite persistent completion. Existence checks use metadata only and do not
read media content. Internal SID/session, proofs, nonces, approval digests,
executable identity, configuration and Lease quota are never serialized.

Reads authenticate and use the trusted server SID/session; clients cannot select
an identity. The ordinary writer's bounded failure-aware barrier precedes the
read-only transaction. Reads do not expire authorizations, advance Plans, recover
Runs, consume quota, confirm capture, or invoke recording/UI. Invalid filters or
cursor return 400 `INVALID_ARGUMENT`. Missing gateway, unavailable identity,
writer failure/timeout, broken schema or invalid owned persistent evidence
return 503 `HISTORY_UNAVAILABLE` without storage paths or exception details.
An initialized empty store returns a successful empty page; a failed source is
not replaced by a partial successful page. Existing ordinary history, per-Plan
history, recording and authorization APIs retain their contracts.

## Retention preview

`GET /api/v1/retention/preview` requires the existing `X-Agent-Recorder-Key`.
It evaluates a time rule for ordinary MP4 outputs under the host data directory's
fixed `Videos` root. It does not grant deletion permission or implement cleanup.
App and Headless share the implementation. External output directories, Plan and
future-window outputs, screenshot series and Bundle artifacts are excluded.

An agent intent such as “Show completed ordinary videos older than October 1
under Recorder's Videos directory and estimate their current file bytes” uses:

```http
GET /api/v1/retention/preview?completed_before=2026-10-01T00:00:00Z&limit=20
X-Agent-Recorder-Key: <api-key>
```

Only `completed_before`, `limit`, and `cursor` are accepted. The cutoff is required:
a timestamp with seconds, at most seven fractional digits, and explicit UTC `Z`
or zero offset (`+` must be URL encoded). It cannot be later than the service clock.
The actual age rule is `completed_at_utc < completed_before`, independent of
file creation/last-write dates. Limit defaults to 20 and ranges from 1 to 100.
Unknown/duplicate arguments and invalid times, limits or cursors return 400
`INVALID_ARGUMENT`. No directory, path, identity, execution or deletion argument
is supported. No cleanup/execution route exists.

The service performs one bounded ordinary-source read through the same unified
history indexed query, writer barrier, evidence validation and read-only
transaction. `created_before` narrows candidates to the cutoff; the actual
completion-time rule is evaluated afterward. This internal read disables history
projection's file-existence probe before scope validation; the public history
API keeps its original behavior. Each page evaluates at most `limit` candidates,
including protected/skipped entries. It never scans directories or reads extra
pages to fill qualifying results. `next_cursor` retains unified-history insert
watermarks and keyset order, binding the cutoff, trusted server identity and host
scope. Keep the cutoff unchanged; limit may change. File samples and task states
are live per page, not a cross-page filesystem snapshot.

The standard envelope's `data` contains:

| Fields | Meaning |
| --- | --- |
| `items[].entry_id`, `recording_id`, `status`, `completed_at_utc` | Public identity and actual completion facts |
| `evaluation`, `reason_code`, `age_candidate` | Time-rule result and protection reason; never approval to delete |
| `recorded_path`, `recorded_bytes`, `observed_file_bytes` | Scoped persistent evidence versus nullable current metadata sample; rejected paths are omitted |
| `evaluated_count`, `age_candidate_count` | Historical candidates inspected versus old, evidenced MP4 entries passing lexical Videos scope |
| `observed_candidate_bytes`, `observed_candidate_file_count`, `unknown_candidate_file_count`, `observed_bytes_complete` | Checked sum of reliable samples on this page; unknown samples are not represented as known zero |
| `next_cursor`, `page_size`, `completed_before` | Manual pagination and normalized cutoff |
| `read_only`, `deletion_supported`, `reference_protection_ready`, `physical_identity_protection_ready`, `bundle_included`, `bytes_scope`, `snapshot` | Explicit scope and limitations |

`evaluation` is `age_candidate`, `protected`, or `skipped`. An `age_candidate=true`
entry may remain protected because of ordinary nested relationships, shared output paths, frozen planned targets, or because
the file is missing, inaccessible, linked or changed. Reasons are a whitelist: `not_completed`, `completion_time_unknown`,
`output_evidence_missing`, `completion_not_before_cutoff`, `outside_videos`,
`path_invalid`, `not_mp4`, `path_check_limit`, `reparse_point`,
`parent_not_directory`, `output_not_file`, `parent_missing`, `file_missing`,
`file_inaccessible`, `metadata_unavailable`, `metadata_changed`,
`file_length_changed`, `nested_relationship`, `nested_parent_referenced`,
`nested_reference_invalid`, `shared_output_path`, `planned_output_path`,
`setup_output_path`, `planned_output_template`, `pending_output_use_unknown`,
`hard_linked_output`, `file_identity_unavailable`, `file_identity_changed`,
`resolved_path_mismatch`, `publication_identity_missing`,
`publication_identity_mismatch`, `bundle_evidence_unknown`,
`bundle_generation_in_progress`, `bundle_artifact_reference`,
`bundle_artifact_state_unknown`, and `age_rule_match`. A missing sample never rewrites the
persistent completed status. A reliable changed length is reported separately
and included in the page's sampled-byte sum while the entry stays protected.

Ordinary nested protection is evaluated after existing state/completion/output/age
and lexical path reasons, and before any file attributes or size probe. Any valid
`nested_role`, `parent_recording_id` or `nested_session_id` on the item protects it
as `nested_relationship`, including missing parents, outer recordings with no
child, self references and cycles. Malformed role/ID/session fields protect as
`nested_reference_invalid` using existing field rules. Even with all three fields
null, any ordinary row referencing this recording as its parent protects it as
`nested_parent_referenced`; child status, date, current page and cursor insertion
watermarks do not restrict this existence check. No child IDs or session material
are returned.

After nested protection, an eligible old completed item is protected as
`shared_output_path` if another ordinary recording, recording run output evidence,
required-once execution authorization or future-window authorization has the same
explicit persisted `output_path`. Its own ordinary `recording_id` is excluded;
null paths do not match. This private existence check includes other identities,
all statuses and dates, other pages and rows beyond the candidate watermark.
It returns no matching source, count, other ID, authorization proof or scope.
Protection occurs before any file attributes or length probes; without a match,
the remaining database protections apply before physical checks.

After shared-output protection, a remaining eligible item is protected as
`planned_output_path` when its exact file target is already frozen in a standing
scope (`authorized_capture_scopes.output_directory + frozen_file_name`), recurring
occurrence specification (`recurring_occurrence_execution_specs.frozen_output_file_path`),
required-once specification (`required_once_authorized_specs.output_directory + frozen_file_name`)
or future-window authorization (`output_directory + output_file_name`). All
persisted frozen uses count, including foreign identities and revoked, expired
or ended plans; this does not approve execution or prove current plan validity.
A directory alone, a filename alone or a template for an ungenerated future
file does not match. Matching sources, counts, IDs and proof remain private.
The branch precedes every file probe and contributes an unknown-byte candidate.

The same batch also protects `required_once_setup_intents.output_directory + frozen_file_name` with `setup_output_path`, including before selection/creation approval and without an authorized specification. All identities, sessions, dates and persisted statuses (including rejected/expired) retain conservative use protection. Valid different names do not match but consume the shared bound. V39 adds only the required-once directory/name composite index (original raw path collation on both columns) and its receipt; V1–V38 definitions/checksums/indexes remain unchanged. Four sources share one deduplicated requested-directory batch/read snapshot and a total 500/LIMIT501 before probes, with the existing complete type/byte/NUL/character projection and exact > unknown > template priority. Missing/damaged V39 index/receipt fails closed without read repair. This is potential output use, not current approval, authorization or deletion permission.

Pending output-use protection: after frozen-target protection and before publication-anchor, Bundle and all file probes, `setup_output_path` protects an exact `setup_intents.output_directory + frozen_file_name` even before an authorized specification exists. Early intents with no output fields invent no path. `planned_output_template` conservatively protects names expressible by a recurring setup intent or any immutable recurring Profile version: `prefix-yyyyMMddTHHmmssZ-<64 hex occurrence digest>.mp4`. It means possible configuration output, not a frozen file reference, current authorization or permission to execute. Expired intents and logically deleted/nonlatest Profile versions retain this range protection.

Only the direct parent directory matches, using the existing raw OrdinalIgnoreCase collation and two trailing-separator forms; no LIKE/glob, foreign GetFullPath, schedule enumeration or disk resolution is used. Prefixes follow the renderer's bounded domain rules (including hyphens, underscores and Chinese letters), timestamps must be real dates/times, and Windows case aliases match without Unicode normalization. Different directories, subdirectories, Videos2, drives, prefixes, malformed digests and ordinary old filenames do not match a valid template. Invalid selected filename/prefix/template evidence is locally `pending_output_use_unknown`; exact setup evidence wins over that unknown, which wins over a template. The previous nested/shared/frozen reasons retain precedence. No source IDs, configured prefix fields, counts, proofs or referrer lists are exposed.

V38 adds only three directory-leading indexes and its migration receipt; V1-V37 definitions/checksums/triggers and business data remain unchanged. One parameterized UNION ALL batch selects the three sources in the same SQLite snapshot as the page, nested/shared/frozen facts and anchors. Requested directory forms are deduplicated; a configuration is not joined once per candidate. A page selects at most 500 related output uses in total, and LIMIT 501 fails the entire request with `RETENTION_PREVIEW_UNAVAILABLE` before any file probe. Fixed setup rows in a requested directory also consume this bound even if their valid filename differs. SQL projects only text fields within 255 (fixed name), 128 (prefix) and 200 (template) SQLite characters; oversized/nontext values become local unknown evidence before materialization. SQL projection rejects NUL in complete selected directory/name/prefix/template fields in SQLite before GetString; damaged suffixes cannot be interpreted as truncated valid text. A rejected related row remains unknown and consumes the same 500-row bound. The indexed raw collation alone selects relations; a selected damaged directory is associated through the trusted requested directory and is never treated as canonical evidence. Projection requires text, at most four UTF-8 bytes per permitted SQLite character, no NUL and the existing character limit (directory 1025 including a trailing separator); subsequent UTF-16/domain limits remain unchanged, including 64-Rune Unicode prefixes. Schema/index/collation/receipt faults also fail closed. Unrelated dirty directories are not parsed. Protected items retain age_candidate=true with unknown observed bytes; the original metadata budgets and both explicit content-verification APIs are unchanged. This is a snapshot preview, not a complete reference graph or future deletion lock.

Standing and the required-once API normalize directories with a trailing
separator; future-window ordinarily removes it, preserving the drive root.
The query slices only the candidate raw text into filename and two bounded
parent-directory forms (with/without the trailing separator; a root stays
`C:\` in both), comparing both columns through v29 composite indexes. Recurring
uses its stored full frozen path and a single-column v29 index. The v28 raw
OrdinalIgnoreCase collation is reused unchanged; no LIKE, extra connection,
foreign-scope JSON parsing, readiness probe or path normalization of foreign
rows occurs. Only these existing canonical storage forms are covered. Historical
noncanonical text, textual/physical aliases and not-yet-concrete targets are not
proved free of use. No such preview result grants cleanup permission.

The current ordinary page, nested flags, four explicit plus four frozen-path existence checks and bounded pending output uses are read
in one read-only SQLite transaction/snapshot after the existing writer barrier.
Parameterized batches contain at most 100 candidates; reverse checks use the
v25 parent index, four v28 path indexes and four v29 frozen-target indexes. Missing, invalid or untrustworthy
private reference evidence, provider/query/schema/receipt/collation failure
returns the entire existing 503 without file probes or read repair. v28 adds
only four indexes and its receipt; v29 only adds the four frozen-target indexes
and its receipt. v1–v28 definitions/checksums, tables, triggers
and business content are unchanged. The centrally registered, fixed versioned
`AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1` collation uses raw
`StringComparer.OrdinalIgnoreCase`, including non-ASCII case comparison, without
fallback to SQLite NOCASE. It does not trim, complete relative paths, normalize
Unicode, resolve links or equate 8.3/UNC/extended-path aliases. Case-sensitive
directories can be conservatively over-protected; textual aliases for the same
physical file remain outside this proof.
Relations can change after this read transaction or between pages; the preview
cannot freeze a future deletion decision. Protected nested, shared-path or frozen-target items passing the age
and lexical rules still have `age_candidate=true`, contribute to the candidate
count and unknown-sample count, but have `observed_file_bytes=null` and add no
bytes to the observed sum. Neither count nor byte sum is safely reclaimable space.

After the existing time/completion, lexical, nested, shared/planned-output and
publication-anchor protections, ordinary Bundle facts protect the MP4 before
any ancestor attribute, leaf-handle or file-existence probe:

| Persisted ordinary `bundle` | Evaluation / reason |
| --- | --- |
| `null` (never registered) | `protected / bundle_evidence_unknown` |
| `generating` | `protected / bundle_generation_in_progress` |
| `ready` | `protected / bundle_artifact_reference` |
| `failed` or `interrupted_unknown` | `protected / bundle_artifact_state_unknown` |
| `not_applicable` | Continue the existing ancestor, handle, identity and length rules |

These protected entries retain `age_candidate=true`, `recorded_path` and
`recorded_bytes`; `observed_file_bytes=null`, and contribute to
`unknown_candidate_file_count`, not `observed_candidate_bytes`. Bundle facts are
the already batched, at-most-100 ordinary facts from the same read-only SQLite
snapshot as the media, references and anchors. Invalid state/path/error tuples,
media bindings, schema or migration receipts fail the entire page with the
existing 503, even if an earlier item reason would protect it. Provider facts
are validated too. No JSON, images, media contents or directories are read to
infer or repair missing facts. Old rows remain null without backfill.

`ready` records a historical association, not current attachment readiness;
failure/unknown cannot rule out partial artifacts. `not_applicable` records that
this recording did not generate a Bundle and proves no absence of other uses.
Facts may change after the transaction or on a subsequent cursor page; they
cannot freeze a future deletion decision. Metadata, marks, first/last frames and
thumbnails have a recorded five-item generation list, without per-attachment
physical identity anchors or a complete cross-artifact reference graph. `bundle_included=false` still means attachment
entities and sizes are excluded; full attachment references, identities and
deletion protection are not implemented. All existing deletion/reference/
physical readiness flags remain false; preview remains read-only.

Scope comparisons use Windows case-insensitive separator boundaries. Device,
UNC, noncanonical and reserved-component paths are protected without probing.
Paths are capped at 1024 characters and 32 components from the drive root.
Ancestor path-attribute checks include the drive, Videos root and every intervening
parent (at most 32); reparse/junction/symlink or unreliable metadata stops sampling.
`attribute_checks_per_entry_max=32` bounds these ancestor path calls; the two
handle-based basic-attribute queries described below are separate fixed calls.
The leaf then uses one short-lived Windows SafeFileHandle with OPEN_EXISTING,
FILE_READ_ATTRIBUTES and OPEN_REPARSE_POINT. No content/data-read, write or delete
access is requested. Final attributes, logical length, link count, deletion state,
volume plus full 128-bit file ID and last-write time come from that same handle,
sampled twice; no FileInfo.Length or second path-based leaf attribute fallback is
used. FILE_STANDARD_INFO/FILE_ID_INFO and basic attributes are queried twice, with
two final-path queries using fixed 2048-character buffers and no retry. Only a
supported canonical local drive path (optionally stripping the exact standard
`\\?\` prefix) equal to the requested path and within Videos qualifies.

A reliable link count above one protects as `hard_linked_output`; missing/invalid
identity or unsupported/native metadata protects as `file_identity_unavailable`.
Deletion-pending or inconsistent identity/volume/attributes/length/link count/write
time protects as `file_identity_changed` (new reparse/directory or invalid negative
length retains `metadata_changed`). Unavailable/truncated/nonlocal or mismatching
final paths protect as `resolved_path_mismatch`. Known leaf reparse/directory,
missing and access errors retain their existing reasons. These untrusted samples
have null observed bytes and contribute unknown-byte counts. Only a stable,
valid single-link observation supplies length; a reliable recorded-length mismatch
still contributes current sampled bytes while remaining protected. Missing or
invalid provider/result contracts fail the whole page with 503; ordinary native
sampling failures protect individual items. No identity material or other resolved
directory is returned or logged.

Sharing is read-only, without write/delete sharing, but pure attribute access can
bypass data-handle sharing conflicts and does not pin a filename or ancestor chain.
The fixed call/buffer bounds are not a hard wall-clock deadline or cancellation
promise for synchronous native calls. No media decoding, content read, hash,
directory traversal, filesystem write, background timeout worker or process occurs.

`bytes_scope=current_page_age_candidates_mp4_logical_length`: this is neither the
whole Videos directory's size, Bundle size, physical disk allocation nor safely
reclaimable space. Summation overflow, unavailable caller identity, writer barrier
failure/timeout or required database/evidence failure returns an entire 503
`RETENTION_PREVIEW_UNAVAILABLE`, never a successful empty/partial page. File-level
protection is reported per item without raw exceptions or credentials.

Capabilities advertise the scope, read-only status and bounds.
`deletion_supported=false`, `reference_protection_ready=false`, and
`physical_identity_protection_ready=false` remain false even when all samples
match. For ordinary completed outputs, only a receipt issued from the shared
publisher's original CREATE_NEW copy handle can supply a private publication
anchor. WGC continuous final publication and AvFinalizer audio mux and validated
None final publication (including AV split and default FFmpeg video-only terminal
paths) propagate that receipt; intermediate AV video, legacy FFmpeg direct audio, screenshot sequences
remain unanchored. Independent plan/future history now has its own private publication identity, outside the ordinary retention-preview scope.
No existing row is backfilled and no preview observation becomes a publication ID. The Windows physical publisher opens the original copy with write,
read-attributes and DELETE access, sharing read/delete but not write. Commit uses
handle-bound rename/replace inside the existing commit gate; a renamed source
still commits the verified original object, never a replacement at its former
name. Failed publication requests disposition on that original handle before
closing it. Invalid handles or failed disposition retain a residue; cleanup does
not reopen or delete the former path. Optional identity query failure may omit a
receipt after a real original-object commit; commit failure has no path fallback.
These operations do not make parent paths or later external changes atomic.

The page, ordinary nested/shared/planned references and private anchors are read
in one read-only SQLite snapshot with a bounded indexed anchor batch (at most
100 rows). After earlier time/lexical/reference reasons, an unanchored row returns
`protected` / `publication_identity_missing`, with `observed_file_bytes=null`,
before ancestor or leaf probes. An anchored row with a recorded `not_applicable`
Bundle still passes all existing current
metadata, hard-link, stability and resolved-path checks; a different volume or
any part of the full 128-bit file ID returns `publication_identity_mismatch` with
unknown bytes. Only matching identity proceeds to the reliable recorded-length
comparison. Invalid anchor schema, encoding or row binding fails the whole page
safely; no read repairs or private identity material appear in public responses.

`age_rule_match` includes this limited publication-copy comparison. It proves
neither upstream content/video authenticity nor safety against every same-account
process. Atomic ancestor chains, changes after sampling, ID reuse, in-place
same-size writes, memory-mapped writes, all aliases and deletion TOCTOU remain
outside the proof. A future deletion design must separately add complete
references, permissions and explicit local approval. The four explicit path
columns, four persisted frozen uses and ordinary nested relationships are only
partial reference protection. Not-yet-concrete planned outputs or reservations,
Profile configuration references and Bundle/marks/thumbnails remain outside it.
References inserted after the current snapshot are absent from that preview;
a future cleanup must revalidate complete references and physical identity.
An item with no detected reference is not proven free of every reference.
This describes workspace code, not a released v0.1.15 capability.

### Durable ordinary Bundle generation facts

`GET /api/v1/recording-history`, its `/{recording_id}` detail, and ordinary entries of `GET /api/v1/history` include `bundle: null` for older/unrecorded Bundle facts, otherwise a status object with nullable generation-time contents:

```json
{
  "bundle": {
    "status": "ready",
    "path": "D:\\captures\\clip.bundle",
    "error_code": null,
    "contents_evidence_version": 1,
    "contents": [
      {
        "name": "metadata.json",
        "media_type": "application/json",
        "size_bytes": 1234,
        "content_hash": null
      },
      {
        "name": "thumbnail.jpg",
        "media_type": "image/jpeg",
        "size_bytes": 5678,
        "content_hash": null
      },
      {
        "name": "first_frame.png",
        "media_type": "image/png",
        "size_bytes": 9012,
        "content_hash": null
      },
      {
        "name": "last_frame.png",
        "media_type": "image/png",
        "size_bytes": 3456,
        "content_hash": null
      },
      {
        "name": "marks.json",
        "media_type": "application/json",
        "size_bytes": 78,
        "content_hash": null
      }
    ]
  }
}
```

The durable statuses are `generating`, `ready`, `failed`, `not_applicable`, and `interrupted_unknown`. Media `completed` remains independent of Bundle generation: a completed MP4 may have a failed Bundle. `generating` and `not_applicable` are recorded atomically with the ordinary media terminal; later internal results only update the registered generation operation. `failed` exposes an allowlisted stable Bundle error code, not exception text or stderr. A recorded `ready` is the generator's historical result, not current directory completeness, identity or deletion safety. Queries never inspect Bundle images/JSON or rebuild facts from directories.

Facts survive writer drain and restart. At the existing owning-host startup recovery, unsettled `generating` becomes `interrupted_unknown` with `bundle_interrupted_unknown`; its old operation is revoked. Recovery does not regenerate, scan, resume recording or restore authorization. Unrecorded historical rows and all other unified sources remain `bundle: null`. Existing auth, filters, cursors, pagination and media metadata semantics are unchanged. Persistence failures and bounded read/drain failures use the existing unavailable contract; completed media is not evidence that Bundle updates drained. Retention stays preview-only; reference, deletion and physical readiness remain false.


The ordinary history Bundle `contents_evidence_version` is `1` only when its first ready result atomically registered all five outputs in the order shown; `contents` is then exactly five immutable generation facts. Names, media types and positive `size_bytes` come from the production generator's verified, owned staging files, frozen before directory publication and returned only after publication succeeds. No arbitrary attachment paths or extra outputs are supported. These lengths are historical generation bytes, not current disk samples, identity, hashes or safe deletion allowances.

Legacy ready results and compatibility generators without explicit evidence, plus generating/failed/interrupted_unknown/not_applicable, expose both added fields as `null`, not an empty list. Older Bundle facts themselves remain null. Schema v33 is additive; it does not inspect or backfill old attachments. A newly required registration, partial list, wrong version/count/type/size or operation/media/directory association is unavailable/503, not an unknown legacy list. Initial registration and ready status/contents commit atomically; value-equal same-operation replay is idempotent, conflicting or late evidence cannot replace a terminal. Writer queue acceptance is not a durable acknowledgement. Startup recovery revokes unfinished operations without regeneration; existing ready contents survive restart.

History reads batch at most 100 ordinary entries / 500 attachment rows in the existing SQLite snapshot with targets/media/Bundle, without directory enumeration or attachment reads. Auth, queries, target filters and cursors are unchanged. `capabilities.unified_history` publishes `bundle_contents_supported_sources=["ordinary"]` only when ordinary is served, `bundle_contents_evidence_version=1`, `bundle_contents_max=5`, and `bundle_contents_semantics="recorded_generation_bytes_not_current_file_identity"`. Retention still protects ready Bundles and fails on damaged evidence before file probes; `bundle_included=false` and all deletion/reference/physical readiness flags remain false. Recorded attachment sizes do not contribute to observed reclaimable bytes.


Each ordinary history `bundle.contents` item additionally has nullable `content_hash`. A trusted value has exactly four fields: `{"algorithm":"sha256","value":"<64 lowercase hex>","basis":"bundle_publication","size_bytes":9012}`. All five hashes cover the actual bytes successfully copied into the original attachment objects by the formal publisher, using its existing fixed buffer. The complete set is issued only after Flush, continuous original-handle holds, whole-directory no-overwrite commit and post-commit identity checks. It is bound to the recording, exact operation, paths, fixed ordinal/name/size and each original physical identity, then saved atomically with Bundle status, contents, identity and the independent main media digest in SQLite V37.

V37 does not scan files or backfill old rows, including metadata containing a hash. Pre-migration facts, public `Ready(...)`/contents-only results, unbound publication or unavailable native identity remain `content_hash: null`; a missing required modern marker or corrupt/incomplete/cross-bound set fails closed. Ordinary list, detail and unified ordinary projections read one indexed batch in the same SQLite snapshot (at most 100 parents / 500 attachment items, with row 501 detecting overflow), without attachment reads. Other unified sources have no ordinary Bundle evidence.

`bundle_publication` is a historical publication observation. It does not establish a shared video/frame timestamp, capture-original bytes, current attachment integrity, playability, future integrity or deletion authority. Explicit attachment comparisons are described below. `artifacts/check` remains metadata-only; `verify-media` still covers only the main MP4 with its existing 1 GiB / 30 second / one worker contract. Retention remains read-only and protects ready Bundles. This source increment is not included in the public v0.1.15 package.

### On-demand ordinary artifact availability

```http
GET /api/v1/recording-history/{recording_id}/artifacts/check
X-Agent-Recorder-Key: <key>
```

App and Headless provide this authenticated, read-only endpoint for ordinary video only. It accepts **no query parameters**, paths, names or repair requests. Missing/incorrect authentication returns 401 (`UNAUTHORIZED`/`FORBIDDEN`); unknown parameters or invalid IDs return 400 `INVALID_ARGUMENT`, absent records 404 `RECORDING_HISTORY_NOT_FOUND`, and unavailable or damaged history/evidence 503 `RECORDING_HISTORY_UNAVAILABLE`. These failures precede file probes. Without its gateway it returns 503 and capability `supported=false`. Ordinary list and unified history are database-only; ordinary detail separately adds best-effort main-file metadata. None automatically invokes this handle-based artifact check.

The existing writer barrier precedes one read-only SQLite transaction that freezes this ID's media, registered Bundle contents and optional private media publication anchor. No live recording state, initialization, recovery or backfill is consulted. Media `completed`, Bundle `ready` and recorded sizes remain historical generation facts, regardless of this response. No schema or historical row is written, no tool/process or capture is started, and no file content or directory members are read.

The normal `{ok,data,request_id}` envelope contains these `data` fields:

| Field | Meaning |
|---|---|
| `recording_id` | Requested ordinary history ID. |
| `check_started_at_utc`, `check_completed_at_utc` | UTC start and response snapshot times, including database work. |
| `probe_state` | `complete`, `busy`, `timeout`, `cancelled`, or `failed`. Complete means processing ended, not that artifacts passed. |
| `media_history_status` | Frozen `pending_confirmation`, `preparing`, `countdown`, `recording`, `finalizing`, `completed`, `failed`, `cancelled`, `rejected`, `expired`, or `interrupted_unknown`; null when no snapshot was acquired. |
| `media` | One file check with `name="media"`; the response discloses no output path. |
| `bundle.history_status` | Frozen `generating`, `ready`, `failed`, `interrupted_unknown`, `not_applicable`; null for unrecorded Bundle or unavailable snapshot. |
| `bundle.contents_evidence_version` | `1` for the explicit complete generation list, otherwise null. |
| `bundle.contents` | Exactly five checks for known ready version 1, otherwise null, **not an established empty collection**. |
| `read_only`, `metadata_only` | Both true. |
| `content_verified`, `deletion_supported` | Both false. |
| `observation_semantics` | `sequential_point_in_time_metadata_not_atomic_snapshot`. |

Each file check has `name`, nullable historical `recorded_size_bytes`, nullable `observed_size_bytes`, `observation`, `reason_code`, nullable `sampled_at_utc`, and `identity_state`. Known attachments retain the fixed generation order: `metadata.json`, `thumbnail.jpg`, `first_frame.png`, `last_frame.png`, `marks.json`; their paths are derived only from the validated frozen Bundle directory. `sampled_at_utc` is the UTC start of that item's metadata attempt; null means no attempt. An interrupted attempt can have a timestamp and unavailable outcome without a completed leaf sample. Items are sampled sequentially, not at a shared atomic instant.

| `observation` | Meaning |
|---|---|
| `present_size_matches` | A stable metadata sample has the recorded length; inspect identity separately. |
| `missing` | Explicit file/parent not-found result. |
| `size_mismatch` | A stable sample differs from recorded length. |
| `inaccessible` | Access denied or sharing conflict; does not mean missing. |
| `unsafe_path` | Refused lexical/reparse/directory/resolved-path condition. |
| `changed_during_probe` | Metadata/identity changed between same-handle frames or deletion was pending. |
| `unavailable` | Unsupported, malformed, failed or interrupted metadata attempt, or bounded path/ancestor check exceeded. |
| `not_checked` | No completed check, including queued items after cancellation/timeout or ineligible media's attachments. |
| `not_applicable` | Media is not a completed ordinary MP4. |
| `unknown` | Completed MP4 lacks a usable completion/positive recorded length. |

`identity_state` is `match`/`mismatch` for stable media or one of the five attachments compared with its trusted private publication anchor; otherwise it is `unverified` for a valid sample without an anchor, or `unknown` when identity cannot be evaluated. The formal Bundle producer can persist a complete five-item original-object identity set together with ready status and the manifest in SQLite V35. Old history, public `Ready(path)`, manifest-only compatibility results and unavailable native identity remain unanchored; there is no scan or backfill. Same-name, same-size replacement of an anchored attachment reports size matching with identity mismatch. A malformed or incomplete modern identity set fails the history read rather than silently becoming unverified. Media, manifest and attachment anchors are frozen in the same database snapshot; physical observations remain point-in-time metadata checks. Neither match nor size matching proves unchanged contents, playability, audiovisual completeness or safe deletion. Volume/file IDs, anchors, operation IDs, nonce and raw exceptions are never returned.

The complete allowlist of `reason_code` values is:

- `not_checked`, `media_not_applicable`, `media_evidence_unknown`;
- `path_invalid`, `path_check_limit`, `ancestor_check_limit`, `local_drive_unavailable`;
- `ancestor_reparse_point`, `parent_not_directory`, `parent_missing`, `file_missing`, `file_inaccessible`;
- `leaf_reparse_point`, `output_not_file`, `metadata_changed`, `metadata_unavailable`, `resolved_path_unavailable`, `resolved_path_mismatch`;
- `publication_identity_mismatch`, `file_size_mismatch`, `metadata_and_publication_identity_match`, `size_matches_identity_unverified`;
- `probe_busy`, `probe_timeout`, `probe_cancelled`, `probe_failed`.

Noncompleted/nonvideo media and insufficient usable output evidence incur zero file IO. Legacy ready/unknown, generating, failed, interrupted and not-applicable Bundles never acquire five-item evidence from files already present. The check accepts canonical local drive paths, including legitimate user-selected outputs outside Videos; UNC/device/ADS/relative/traversal and reserved-name paths are refused before attributes or leaf opens. The existing 1024-character bound and **64 distinct ancestors across the entire request** apply. Ancestor attributes are cached only within this request. Reparse ancestors, reparse leaves and directories posing as files are refused. Each leaf uses the existing Windows metadata-only single handle (`READ_ATTRIBUTES`, share-read) with before/after attributes, length, identity and final-path checks; no content handle is opened.

One process/host lease permits at most one worker, with no waiting queue; excess requests return `busy`. The **3-second total deadline starts before database dispatch**. Timeout or cancellation freezes an immutable partial response: finished samples remain, the active attempt is unavailable, remaining items are not checked, and no next item starts after cancellation. Native/SQLite synchronous work may outlive the response. The lease remains occupied until the worker actually exits, including after API stop and service/API recreation. No previous request's samples are reused. API shutdown explicitly cancels the request. Confirmed connection errors/RST trigger best-effort cancellation; a send-only FIN does not imply that the client abandoned receiving. Graceful Close and send half-close cannot reliably be distinguished from EOF alone, so ambiguous cases remain subject to the same total deadline, without an extra waiting period.

`capabilities.ordinary_artifact_check` publishes `supported`, this `endpoint`, `scope="ordinary_video"`, `max_files=6`, `max_distinct_ancestors=64`, `deadline_ms=3000`, `metadata_only=true`, `content_verification=false`, `deletion_supported=false`, `observation_semantics="sequential_point_in_time_metadata_not_atomic_snapshot"`, and `identity_scope="media_and_five_bundle_publication_anchors"`. This describes workspace code, not a released capability. SQLite facts and subsequent file observations are separate snapshots; ancestor chains are not atomically locked. Changes after sampling, in-place same-size writes, ID reuse, aliases and directory TOCTOU remain outside the result. Retention still protects ready Bundles, preserves unknown-byte accounting and all deletion/reference/physical-readiness flags; a check cannot authorize cleanup or bypass references.

Bundle identity matching does not change retention protection: registered Bundle protection is applied before physical sampling and never becomes delete permission. This API does not expose receipt or identity material and does not perform content hashing or playback checks.


### Plan and future main-media check (workspace source, not released)

```http
GET /api/v1/history/{entry_id}/artifacts/check
X-Agent-Recorder-Key: <key>
```

App and Headless support `plan:<occurrence_id>` and `future_window:<authorization_id>` from the current SID/session. Encode the complete entry ID as **one URI segment**, exactly once. Canonical recurring IDs include `recurring-occurrence/v1:<64 lowercase hex>`; its slash is encoded in the URI, then accepted only as part of that exact ID format. Arbitrary separators, remaining percent escapes, double encoding, unsupported sources, query parameters and any body are rejected. Ordinary entries use their existing endpoint above.

Authentication failures are 401; invalid/unsupported arguments are 400 `INVALID_ARGUMENT`; absent or foreign identity/session entries are 404 `HISTORY_NOT_FOUND`; a missing gateway or invalid durable facts is 503 `HISTORY_UNAVAILABLE`. Error messages disclose no paths, native identities, SQL or private authorization material. These failures precede media probes.

An exact indexed read-only SQLite snapshot validates original ownership, lifecycle, the Plan locator and frozen output, V40/V41 publication evidence and original successful size. The new fact read disables old existence projections. Standing/recurring sizes come from verified V41 facts even when native identity is unknown; required-once and future sizes are cross-checked against their own successful execution. Modern damaged/missing markers fail closed; legacy unknown evidence is not repaired. Non-completed or insufficient evidence is not sampled; an unknown historical size is never invented.

`data` contains `entry_id`, `source`, `check_started_at_utc`, `check_completed_at_utc`, `probe_state`, `media_history_status` and `media` with the same file-check fields described above. There is no Bundle object or output path. Fixed flags are `scope="main_media"`, `read_only=true`, `metadata_only=true`, `content_verification=false`, `bundle_included=false`, `deletion_supported=false`. `complete` means processing finished. Identity is `match`, `mismatch`, `unverified` or `unknown`; missing media does not change historical completion.

New and ordinary metadata checks share **one actual host worker without a queue**, a 3-second deadline starting before database dispatch and up to 64 ancestors. The new request opens at most one READ_ATTRIBUTES leaf; it also rejects multiple links. Timeout, disconnect or shutdown freezes the returned result while the actual worker keeps its slot until resources retire. The separate 30-second content-verification worker and ordinary six-file DTO remain unchanged. A metadata handle can coexist with some data writers despite share-read; stable samples do not establish content integrity.

`capabilities.history_artifact_check` reports gateway support, supported sources, endpoint, `scope="main_media"`, `max_files=1`, `max_distinct_ancestors=64`, `deadline_ms=3000`, `max_concurrent=1`, `concurrency_scope="host_metadata_check"`, the shared ordinary endpoint and fixed flags. A missing gateway advertises no supported sources. Headless can read historical Plan/future facts without gaining capture authorization; its ordinary-only unified page capability remains unchanged.

`observation_semantics="sequential_point_in_time_metadata_not_atomic_snapshot"`: this is a current metadata observation after a database snapshot. It reads no media content, enumerates no directory and starts no process/capture. It does not prove audio completeness, playback, future stability, absence of references or safe deletion. This source increment has passed independent acceptance; it is not in the public v0.1.15 package.

### Durable ordinary media generation digest (workspace V36)

The ordinary entries returned by `GET /api/v1/recording-history`, its `/{recording_id}` detail (`data.recording`), and `GET /api/v1/history` include a top-level nullable `media_hash`:

```json
{"media_hash":{"algorithm":"sha256","value":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","basis":"bundle_generation","size_bytes":1048576}}
```

The value records the main MP4 bytes observed during successful Bundle generation. It requires the internal ordinary MP4 publication receipt (the FFmpeg paths and ordinary WGC paths above), the registered recording/Bundle operation, and before/after native metadata on the same content read handle. The existing hash pass streams a fixed 64 KiB buffer with cancellation and denies shared writes/deletes. Its result is committed with the successful Bundle status, manifest and identities in one history transaction. It agrees with the existing `metadata.json` `media.sha256` for that generation; reading metadata alone cannot grant trusted evidence.

Legacy history, absent or failed Bundles, public fake results and unavailable trusted native bindings return `null`; no media is scanned or backfilled during migration or history queries. A damaged stored marker or digest makes the history request unavailable rather than returning a trusted value. Queries use the same SQLite snapshot and an indexed batch of at most 100 entries. Other unified sources return `null`. No hash filter is provided. Content comparison requires the explicit POST described below.

`capabilities.recording_history.media_hash_supported` and `capabilities.unified_history.media_hash_supported_sources` advertise ordinary support, with `media_hash_algorithm="sha256"` and `media_hash_basis="bundle_generation"`. This is a generation observation, not capture-original bytes or a current integrity/playability assertion. Changes after the hash pass, the three separate frame extractions and all attachments are outside this digest. Artifact checks retain `metadata_only=true`, `content_verification=false`; retention remains read-only and protects ready Bundles. The digest grants no deletion authority and is not in the public v0.1.15 package.

### Explicit ordinary main-media verification

`POST /api/v1/recording-history/{recording_id}/artifacts/verify-media` requires the configured API key and current loopback host. Send no body or `{}`. Query parameters and all object fields (including paths, algorithms, hashes and budgets) are rejected with `400 INVALID_ARGUMENT` before verification. Invalid IDs use the existing ordinary-history rules; missing entries return `404 RECORDING_HISTORY_NOT_FOUND`. Unconfigured or damaged history returns `503 RECORDING_HISTORY_UNAVAILABLE` without exposing native errors or paths.

The usual envelope contains:

```json
{"ok":true,"data":{"recording_id":"...","check_started_at_utc":"...","check_completed_at_utc":"...","check_state":"completed","comparison":"match","reason_code":"digest_match","bytes_read":160003,"recorded_hash":{"algorithm":"sha256","value":"<64 lowercase hex>","basis":"bundle_generation","size_bytes":160003},"observed_hash":{"algorithm":"sha256","value":"<64 lowercase hex>","size_bytes":160003},"content_verified":true,"scope":"main_media","read_only":true,"deletion_supported":false,"observation_semantics":"single_read_handle_point_in_time_comparison_not_future_integrity_guarantee"},"request_id":"..."}
```

`check_state` is `completed`, `busy`, `timeout`, `cancelled` or `failed`. `comparison` is `match`, `mismatch` or `not_checked`. Only a complete bounded SHA-256 read with stable before/after native metadata, canonical final path, original physical identity and matching generation digest returns `content_verified=true`. A complete stable read of the original object with different bytes returns `digest_mismatch` / `mismatch`. Replacement objects and changed lengths instead return `publication_identity_mismatch` or `file_size_mismatch`, with `not_checked`, no observed digest and zero content reads. Missing trusted generation evidence returns `recorded_hash_unavailable` without opening the media. Unfinished/non-MP4 history returns `media_not_applicable`.

Other stable reasons include `publication_anchor_unavailable`, `media_evidence_unknown`, `media_byte_limit`, `path_invalid`, `path_check_limit`, `ancestor_check_limit`, `ancestor_reparse_point`, `parent_not_directory`, `local_drive_unavailable`, `file_missing`, `file_inaccessible`, `unsafe_or_unknown_handle_metadata`, `metadata_unavailable`, `read_length_mismatch`, `metadata_changed`, `verification_failed`, `verification_busy`, `verification_deadline` and `verification_cancelled`. A reason describes this attempt, not permanent file health. Partial, unstable, cancelled or timed-out work has `observed_hash=null`, `comparison=not_checked`, `content_verified=false`; `bytes_read` is progress frozen at the response, not an assurance of a full read. `recorded_hash` can be null if facts were unavailable before the deadline or no trusted digest exists.

`capabilities.ordinary_media_verification` separately publishes `supported`, `endpoint`, `scope="main_media"`, `algorithm="sha256"`, `reference_basis="bundle_generation"`, `max_bytes=1073741824`, `deadline_ms=30000`, `max_concurrent=1`, `read_only=true`, `deletion_supported=false` and the observation semantics above. The deadline starts before SQLite dispatch and covers handle validation and disposal. One process-wide worker does not queue; a timed-out or cancelled worker retains its slot until actual exit, including across service/gateway recreation. Confirmed client disconnects and host shutdown request cancellation. A send-only FIN cannot prove that the receiving peer abandoned its response, so it remains bounded by the total deadline.

One existing leaf is opened read-only with `OPEN_REPARSE_POINT` and sharing only READ, then native facts are checked on that very content handle before any content read. The fixed 64 KiB buffer streams at most 1 GiB; the final read is bounded to the frozen remaining length. Unsafe paths, reparse points, directories, hard links and unknown native evidence fail closed. At most 64 ancestors are checked; those checks do not atomically lock directories or eliminate namespace TOCTOU or future ID reuse. The comparison only covers bytes observed on this handle against `basis=bundle_generation`. It does not establish capture-original bytes, playability, audio/video completeness, attachment integrity, a future integrity guarantee or safe deletion. No result is persisted or used to repair/backfill history. Existing history GETs, metadata-only artifacts/check and retention preview do not invoke this operation. No FFmpeg, playback, capture or new human confirmation is involved.


### Explicit on-demand Bundle attachment verification (source V37)

`POST /api/v1/recording-history/{recording_id}/artifacts/verify-attachments` compares the fixed ordered five attachments (`metadata.json`, `thumbnail.jpg`, `first_frame.png`, `last_frame.png`, `marks.json`) with their durable `bundle_publication` SHA-256 evidence. Send an empty body or `{}` with `X-Agent-Recorder-Key`. A configured key is required even when ordinary authentication is disabled. The current loopback host owns the request. All query parameters and JSON fields, including duplicate fields and caller paths, names, algorithms, hashes or budgets, are rejected before database dispatch. Invalid input/ID returns 400; absent ordinary ID returns 404; unconfigured or corrupt history returns 503 with sanitized errors.

Only a completed ordinary MP4 with a ready Bundle and the complete trusted V37 attachment hash/identity tuple can enter file reads. One SQLite transaction freezes terminal, contents, hash and original publication identities. Legacy/unknown/nonready facts return `not_checked` without reading or reconstructing evidence from metadata or directories. Verification does not persist a permanent verified flag.

The **whole request** admits at most five files, 64 distinct ancestors, **67,108,864 bytes (64 MiB)** and **30,000 ms**, including SQLite dispatch, native calls, reads and actual handle closure. Checked trusted sizes exceeding the aggregate limit are refused before opens. Local canonical paths and drive policy apply; reparse points, non-directory ancestors, non-file leaves, hardlinks, delete-pending or unknown native facts are rejected. Each item uses one sequential OPEN_REPARSE_POINT/OVERLAPPED handle with FileShare.Read, a 64 KiB buffer and SHA-256. Original identity and length are checked before reading; foreign replacements or wrong lengths are not hashed. The same handle's pre/post native metadata, actual Position/Length and closure must be stable before an observed hash is exposed.

Attachments and `verify-media` share **one host-wide worker without a queue**. Either endpoint may return `busy` while the other works, including after gateway/service recreation. Timeout, explicit disconnect/RST or host stop cancels the request; ambiguous send-only FIN retains the existing receive-side semantics. Timed-out/cancelled blocked SQLite/native/read work retains the slot until its actual worker exits and all handles close. Main MP4 limits remain 1 GiB / 30 seconds.

The response contains `recording_id`, UTC `check_started_at_utc`/`check_completed_at_utc`, `check_state` (`completed`, `busy`, `timeout`, `cancelled`, `failed`), `comparison` (`match`, `mismatch`, `not_checked`), `reason_code`, aggregate `bytes_read`, `items`, `content_verified`, `scope="bundle_attachments"`, `read_only=true`, `deletion_supported=false` and `observation_semantics`. Items are empty before facts are available and thereafter have the fixed five names/order. Each item exposes its state/comparison/reason, bytes, optional UTC read-observation bounds, a recorded hash `{algorithm,value,basis:"bundle_publication",size_bytes}`, a complete stable observed hash `{algorithm,value,size_bytes}` or null, and `content_verified`. No private file/volume ID, operation, receipt, path or native exception is returned.

`digest_match` means that item's full stable read matched its recorded digest; `digest_mismatch` identifies a same-object, same-length byte change. Replacement/size/unsafe/missing/inaccessible/short-read/metadata-drift conditions remain unverified. The first nonmatch stops subsequent reads; earlier closed complete observations may be retained and remaining items are `not_checked`. Timeout/cancellation freezes progress: later worker completion cannot mutate it or expose an unfinished item's prefix hash. The request is verified/match only when all five matched, closed and remained within its deadline/cancellation scope.

These are **sequential handle observations**, not a simultaneous or atomic five-file snapshot; earlier files may change before the response. They do not prove a shared frame timestamp, playability, future integrity or deletion authority, and combining them with the main endpoint does not create an atomic six-file snapshot. History GET, retention and `artifacts/check` do not invoke content verification; the latter remains metadata-only.

`capabilities.ordinary_attachment_verification` separately publishes support, endpoint, scope, `algorithm="sha256"`, `reference_basis="bundle_publication"`, `max_files=5`, `max_distinct_ancestors=64`, `max_bytes=67108864`, `deadline_ms=30000`, `max_concurrent=1`, `concurrency_scope="host_content_verification"`, `shared_with` pointing to `verify-media`, read-only/deletion flags and sequential observation semantics. This describes workspace source, not the public v0.1.15 package.

Example admission response (no snapshot acquired while the shared worker is busy):

```json
{"recording_id":"rec_example","check_started_at_utc":"2026-10-12T01:00:00Z","check_completed_at_utc":"2026-10-12T01:00:00Z","check_state":"busy","comparison":"not_checked","reason_code":"verification_busy","bytes_read":0,"items":[],"content_verified":false,"scope":"bundle_attachments","read_only":true,"deletion_supported":false,"observation_semantics":"sequential_read_handle_point_in_time_comparisons_not_simultaneous_or_atomic_snapshot_earlier_files_may_change_before_response_not_future_integrity_guarantee"}
```

### Plan Media Publication Identity

Successful standing, recurring and required_once Plan MP4 executions preserve the original final publisher receipt inside the existing winner transaction. Native identity remains private; legacy or legitimately unavailable native identity stays unknown. Plan status, paged Plan history and unified source=plan history validate selected private facts in their existing read snapshot (512/100/100 bounds); modern missing or corrupt facts fail closed through the existing sanitized unavailable/503 contract. Public DTOs, cursors, endpoints and capabilities are unchanged. History GET does not read media bytes, hash or backfill identity. A recorded original identity is a publication-time fact; it does not prove current bytes, playback or deletion authority. This source increment has passed independent acceptance; the public v0.1.15 package is unchanged.
