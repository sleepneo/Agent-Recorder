# Quick Start - Agent Recorder

Agent Recorder is an **AI agent-native local screen recording capability layer**.
The normal path is: human speaks, local AI agent calls the quick API, Agent
Recorder asks for local selection/confirmation, then writes an MP4.

## How To Use

1. Download and extract the Windows portable zip.
2. Ask your local AI agent to read:
   - `AGENT-INSTRUCTIONS.zh-CN.md`
   - `AGENT-API-REFERENCE.zh-CN.md`
3. Tell the agent what to record:

```text
Record a selected region for 30 seconds.
```

or:

```text
Record the current conversation window for 5 minutes.
```

4. The AI agent should:
   - run `AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json`
   - read the API key from the returned `api_key_file`
   - call `POST /api/v1/recordings/quick`
   - poll `/confirmations/{id}` until the local user approves or rejects
   - poll `/recordings/{id}` until completion
   - report the final MP4 path

5. The human user only selects the region, confirms recording locally, and plays
   the returned MP4. In the selection UI, drag a custom rectangle or click a
   highlighted window; edges snap automatically, and holding `Alt` temporarily
   disables snapping for precise adjustment.

## Quick API Targets

| target.type | Use case |
| --- | --- |
| `primary_display` | Record the primary display |
| `active_window` | Record the current active window using its visible bounds |
| `selected_region` | Ask the user to draw a region, then record it |

Example request:

```json
{
  "target": { "type": "selected_region", "selection_timeout_seconds": 120 },
  "duration_seconds": 30,
  "countdown_seconds": 3,
  "video": { "fps": 30, "quality": "medium" }
}
```

`countdown_seconds` is optional for both raw and quick recording requests. It
defaults to `3` and accepts only integer values `0` through `10`. Use `0` to
skip the visible countdown while keeping local confirmation, preparation,
preflight, and credible-first-frame gating. The value is shown in the local
confirmation summary and returned under recording `config`.

## Files

When started through `AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json`
from the portable package, the default data directory is
`<package-root>\.local-data`:

- API key: `.local-data\config\api-key.txt`
- Videos: `.local-data\Videos\`
- Audit log: `.local-data\logs\audit.jsonl`

If `AgentRecorder.App.exe` or `AgentRecorder.Headless.exe` is launched directly
without `AGENT_RECORDER_DATA_DIR`, the default data directory is
`%LOCALAPPDATA%\AgentRecorder`. Agents should trust the returned `data_dir` and
`api_key_file` fields.

## Safety

- API binds to `127.0.0.1`.
- State-changing calls require `X-Agent-Recorder-Key`.
- AI agents can request recording but cannot silently approve it.
- Local user confirmation is mandatory. The confirmation window also lets the user choose the save directory for that recording.
- HTTP self-approval is blocked with `405 METHOD_NOT_ALLOWED`.

Before showing confirmation and again before capture starts, Agent Recorder
checks the output path, free space, encoder availability, capture bounds, and
whether the selected source still exists.

## Stopping Recordings

While a recording is active, the tray icon turns red and shows the recording
state. You can stop recordings in three ways:

1. **Floating stop button**: a small red stop button appears near the top-right
   corner of each recording region. Click it to stop only that recording.
2. **Tray menu**: right-click the tray icon and choose "Stop recording" (one
   active recording) or "Stop all recordings (N)" (multiple recordings).
3. **Global hotkey**: press `Ctrl+Shift+F10` to stop all active recordings.

For ordinary recordings, the REC border and floating stop button remain visible
to the user while being excluded from the captured video on a best-effort basis.
During nested recording, a geometrically safe inner control can remain visible
in the outer video so the outer recording captures the complete inner workflow.
The stop control may move inside the recording boundary when the preferred
outside position would leave its display; capture exclusion behavior is unchanged.

The agent can also stop a specific recording through the API:

```http
POST /api/v1/recordings/{recording_id}/stop
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "reason": "user_requested"
}
```

## Adding Chapter Marks

While recording, press `Ctrl+Shift+F11` to add a chapter mark. Agent Recorder
shows brief green in-app feedback; it does not use a tray balloon. If outer and
inner recordings are active, one key press adds a mark to each recording on its
own first-frame timeline.

An agent can add a labeled mark to one active recording through the
authenticated API:

```http
POST /api/v1/recordings/{recording_id}/marks
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "label": "Important decision"
}
```

Accepted marks are written to `<video-stem>.bundle/marks.json` after a
successful FFmpeg MP4 recording.

The tray menu provides a Chinese/English language selector. The choice is
persisted locally and applies to newly opened selection, confirmation, and
recording-control windows.

## Public System Audio

System audio is public and still requires the same local confirmation as every
recording. A quick request can use the current Windows multimedia output:

```json
{
  "target": { "type": "selected_region" },
  "duration_seconds": 30,
  "audio": { "system_audio": { "enabled": true } }
}
```

The raw endpoint uses the same audio object:

```json
{
  "source": { "type": "display", "display_id": "display_1" },
  "stop_condition": { "type": "duration", "seconds": 30 },
  "audio": { "system_audio": { "enabled": true } }
}
```

Omit `audio.system_audio.device_id` to use the current Windows multimedia
output. For an explicit endpoint, use an exact `id` from
`GET /api/v1/audio/devices` → `output_devices`; only active render endpoints
are listed. Microphone and system audio are mutually exclusive. The endpoint
shown in local confirmation is fixed for that recording: changing the Windows
default output does not switch capture, and returning to the approved endpoint
uses bounded recovery with any silence gap reported in continuity metadata.

## Bounded Screenshot Series

Use the same quick endpoint for a bounded PNG sequence. The local user still
confirms the request:

```json
{
  "target": { "type": "selected_region" },
  "mode": "screenshot_series",
  "interval_ms": 5000,
  "max_count": 12,
  "countdown_seconds": 3
}
```

The response exposes `mode: "screenshot_series"` and the planned/captured
counts. The final output is a directory containing numbered PNG files and
`series.json`; audio and chapter marks are intentionally unsupported. Use
`max_duration_seconds` instead of `max_count` when a duration bound is more
natural, but never send both.
The schedule is anchored at the first valid PNG submission. Each scheduled
point is claimed serially and launches one bounded FFmpeg single-frame process;
there is no parallel catch-up. In `series.json`, `lateness_ms` measures delay at
the claim point, while `capture_duration_ms` measures the monotonic time from
claim to valid PNG submission. Neither is a fixed desktop-latency guarantee.

## Portable Package Contents

```text
AgentRecorder.App\                 desktop application and local UI
AgentRecorder.Headless\            advanced non-interactive service host
AgentRecorder.Cli\                 agent startup/readiness helper
AgentRecorder.AudioHelper\         isolated Windows WASAPI audio helper
README.md                          English overview
QUICKSTART.md                      this guide
AGENT-INSTRUCTIONS.zh-CN.md        agent operating instructions
AGENT-API-REFERENCE.zh-CN.md       agent API reference
LICENSE                            Agent Recorder MIT license
LICENSE-NOTICE.md                  third-party license notices
```
