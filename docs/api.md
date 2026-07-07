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

## Capabilities

```http
GET /capabilities
```

The response includes:

- app name/version/platform
- host mode and autostart status
- FFmpeg resolution and prewarm status
- recording source support: `display`, `window`, `region`
- quick recording endpoint and recipes
- safety and auth policy
- readiness data when available

Quick recipe fields:

```json
{
  "interaction": {
    "quick_recording_supported": true,
    "quick_recording_endpoint": "/api/v1/recordings/quick",
    "quick_recipes": [
      { "name": "record_primary_display", "target_type": "primary_display" },
      { "name": "record_active_window", "target_type": "active_window" },
      { "name": "record_selected_region", "target_type": "selected_region" }
    ]
  }
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
  "audio": {
    "microphone": { "enabled": false }
  },
  "output": {
    "directory": "default",
    "filename_template": "recording-{datetime}"
  }
}
```

Supported `target.type` values:

| target.type | Behavior |
| --- | --- |
| `primary_display` | Resolve the primary display, then create a recording |
| `active_window` | Resolve the active window, clamp its visible bounds to the virtual desktop, then create a recording |
| `selected_region` | Show local region-selection UI, then create a recording |

Successful creation returns `requires_user_confirmation`:

```json
{
  "status": "requires_user_confirmation",
  "confirmation_id": "conf_xxx",
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
  "audio": { "microphone": { "enabled": false } },
  "video": { "fps": 30, "quality": "medium" }
}
```

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

## Confirmation And Status

```http
GET /confirmations/{confirmation_id}
GET /recordings/{recording_id}
GET /recordings/{recording_id}/output
POST /recordings/{recording_id}/stop
```

Recording states:

| State | Meaning |
| --- | --- |
| `pending_confirmation` | Waiting for local user confirmation |
| `recording` | Recording is active |
| `stopping` | Stop requested |
| `completed` | Recording completed |
| `failed` | Recording failed |
| `cancelled` | Recording cancelled |
| `rejected` | User rejected the confirmation |
| `expired` | Confirmation timed out |

HTTP confirmation approval is intentionally blocked:

```http
POST /confirmations/{id}/approve
```

returns `405 METHOD_NOT_ALLOWED`. The local user must confirm via local UI.

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
