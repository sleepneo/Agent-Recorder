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
- Microphone/system audio support is not a completed product path yet.
