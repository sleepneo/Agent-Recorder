# Agent Recorder Safety Model

Agent Recorder is designed for local AI-agent use. The agent may request a
recording, but the local user remains in control of what is selected and whether
recording starts.

## Safety Boundaries

| Boundary | Implementation |
| --- | --- |
| Local-only API | Server binds to `127.0.0.1:37891` |
| API key | State-changing and sensitive endpoints require `X-Agent-Recorder-Key` |
| Local confirmation | Every recording request enters a local confirmation flow |
| HTTP self-approval blocked | `POST /confirmations/{id}/approve` returns `405 METHOD_NOT_ALLOWED` |
| Region selection | Selected-region recording uses local UI controlled by the user |
| Audit log | Recording and confirmation events are written to local JSONL logs |
| Path policy | Unsafe output paths are rejected |
| Sensitive windows | Known sensitive titles/processes are blocked by policy |

## Confirmation Flow

1. Agent calls `POST /api/v1/recordings/quick` or `POST /api/v1/recordings`.
2. Agent Recorder creates a pending confirmation.
3. The local user approves or rejects through local UI.
4. Recording starts only after approval.
5. The agent polls status and reports the result.

The agent must not say "recording has started" while the state is still
`pending_confirmation`.

## API Key Storage

When started through the portable CLI, the default data directory is:

```text
<package-root>\.local-data
```

and the key is stored at:

```text
.local-data\config\api-key.txt
```

If the app or headless host is launched directly without
`AGENT_RECORDER_DATA_DIR`, the default data directory is
`%LOCALAPPDATA%\AgentRecorder`. Agents should use the `api_key_file` path
reported by `ensure-running` or `/capabilities`.

The key authenticates local API calls. It is not a substitute for local user
confirmation.

## Audit Data

Audit logs are written under the active data directory:

```text
<data-dir>\logs\
```

Typical event categories:

- service start/stop/readiness
- recording requested/started/stopped/completed/failed
- confirmation created/approved/rejected/expired
- region selection unavailable/cancelled/selected
- FFmpeg prewarm status

## Performance Diagnostics

Local performance traces are written separately from audit logs:

```text
<data-dir>\perf\recording-traces.jsonl
```

These traces are local diagnostic data only. They record stage events such as `intent.accepted`, `confirmation.shown`, `capture.start_requested`, `capture.backend_start_returned`, and `capture.first_frame_observed` to help diagnose latency between the agent's request and capture backend startup. They are **not** a recording audit, do not contain API keys, full output paths, window titles, the full FFmpeg command line, or raw progress text, and do not affect the confirmation or recording state machines.

`capture.first_frame_observed` is only emitted for the default FFmpeg video capture path. It reports non-sensitive numeric progress evidence (`frame_number`, `total_size_bytes`, optional `out_time_us`) and is produced at most once per trace. It is not evidence of physical disk flush, screen-capture exact delivery, or output-file validity.

The optional `X-Agent-Sent-At` header is treated as an untrusted client hint and is isolated from server-side latency percentiles.

When the agent forwards an `X-Agent-Recorder-Ensure-Context` header from a successful `ensure-running` result, the server reads and one-time-consumes a short-lived local context file (`<data-dir>\runtime\ensure-contexts\<id>.json`) and may add `startup_kind`, `ensure_elapsed_ms`, `service_startup_elapsed_ms`, and `ensure_context_status` to the performance trace. The raw context ID, context file path, ready file content, and header literal are never written to the performance trace or audit log. A missing, expired, malformed, identity-mismatched, deletion/claim-failed, or already-consumed context does not affect the confirmation or recording state machines. For concurrent or duplicate consumption of the same context ID, only one trace receives `consumed` and the trusted startup fields; the others receive `reused` or `missing`.

Context files are written using a random temp file in the same directory and atomically moved into place; temp files are cleaned up on failure paths. Both context files and the in-memory consumption tombstones have a 5-minute TTL and a count limit so they do not grow without bound. `ensure-running` error results omit `startup_kind`, `ensure_elapsed_ms`, `ensure_context_id`, `ensure_context_header`, and `ensure_context_available`.

## Agent Rules

- Use `POST /api/v1/recordings/quick` for common natural-language requests.
- Explain the target and duration before requesting recording.
- Let the user complete selection and confirmation locally.
- Never call blocked HTTP approval/rejection endpoints.
- Stop polling and report clearly if the user rejects or confirmation expires.

## Known Limitations

- Current builds target Windows.
- The portable package is not code-signed.
- Some GPU-accelerated windows may not capture reliably through FFmpeg
  `gdigrab`.
- Microphone recording uses an isolated Windows WASAPI helper by default and is
  muxed into the final MP4 as AAC. The helper process and its capture stream do
  not start until after local user confirmation; FFmpeg dshow remains available
  only as an explicit diagnostic fallback. System audio recording is not
  implemented; requests that set `audio.system_audio.enabled=true` fail fast
  with `CAPABILITY_NOT_IMPLEMENTED` and never reach confirmation or capture.
- Microphone discovery supports both the bundled FFmpeg classic dshow listing and the FFmpeg 8.x tagged format. The parser accepts only trusted logger prefixes and complete records; malformed, incomplete, or conflicting listings fail closed as `unavailable`, and partial device lists are never returned. See [API reference](api.md#get-audiodevices) for the exact grammar and response contract.
