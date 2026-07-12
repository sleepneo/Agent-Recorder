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
  "output": { "path": "...", "duration_seconds": 300.0 },
  "wait": {
    "requested_ms": 25000,
    "elapsed_ms": 15200,
    "timed_out": false
  },
  "next_poll_hint_ms": null
}
```

New fields:

| Field | Description |
|-------|-------------|
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
