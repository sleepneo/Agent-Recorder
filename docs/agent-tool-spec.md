# Agent Tool Specification

This document describes the tool shape a local AI agent can expose for Agent
Recorder. The tool should map natural-language recording requests to
`POST /api/v1/recordings/quick` first, and only use lower-level endpoints when
the user asks for precise control.

## Tool: record_screen

### Purpose

Start a screen recording request through Agent Recorder. The request still
requires local user confirmation before recording starts.

### Parameters

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `target_type` | string | Yes | `primary_display`, `active_window`, `selected_region`, or `last_region` |
| `duration_seconds` | integer | No | Recording duration. If omitted, recording is manual-stop |
| `selection_timeout_seconds` | integer | No | Timeout for `selected_region`, default `120` |
| `fps` | integer | No | `15`, `24`, `30`, or `60`, default `30` |
| `quality` | string | No | `low`, `medium`, or `high`, default `medium` |
| `microphone_enabled` | boolean | No | Set to `true` to include microphone audio (AAC). Defaults to `false`. Maps to `audio.microphone.enabled`. System audio is not implemented. |
| `microphone_device_id` | string | No | Required when multiple active microphones are available and `microphone_enabled=true`, or when the default/active device cannot be uniquely determined. Must be the `id` returned by `GET /api/v1/audio/devices`. Maps to `audio.microphone.device_id`. If the selected device is muted or known inactive, the request fails before any UI is shown with `AUDIO_DEVICE_MUTED` or `AUDIO_DEVICE_NOT_AVAILABLE`. A low-volume warning is shown when the device is unmuted but `volume_percent < 10`; this does not block recording. |
| `nested_role` | string | No | `outer` or `inner` for nested recording |
| `parent_recording_id` | string | No | Required for nested inner recordings |
| `session_id` | string | No | Optional nested recording session id |

### JSON Schema

```json
{
  "name": "record_screen",
  "description": "Request a local screen recording through Agent Recorder. Recording starts only after local user confirmation.",
  "parameters": {
    "type": "object",
    "properties": {
      "target_type": {
        "type": "string",
        "enum": ["primary_display", "active_window", "selected_region", "last_region"]
      },
      "duration_seconds": {
        "type": "integer",
        "minimum": 1,
        "maximum": 7200
      },
      "selection_timeout_seconds": {
        "type": "integer",
        "minimum": 10,
        "maximum": 600,
        "default": 120
      },
      "fps": {
        "type": "integer",
        "enum": [15, 24, 30, 60],
        "default": 30
      },
      "quality": {
        "type": "string",
        "enum": ["low", "medium", "high"],
        "default": "medium"
      },
      "microphone_enabled": {
        "type": "boolean",
        "default": false,
        "description": "Set to true to include microphone audio (AAC). Maps to audio.microphone.enabled."
      },
      "microphone_device_id": {
        "type": "string",
        "description": "Required when multiple active microphones are available and microphone_enabled is true, or when the default/active device cannot be uniquely determined. Must be the id from GET /api/v1/audio/devices. Maps to audio.microphone.device_id."
      },
      "nested_role": {
        "type": "string",
        "enum": ["outer", "inner"]
      },
      "parent_recording_id": {
        "type": "string"
      },
      "session_id": {
        "type": "string"
      }
    },
    "required": ["target_type"]
  }
}
```

### API Mapping

Tool input:

```json
{
  "target_type": "selected_region",
  "duration_seconds": 60,
  "selection_timeout_seconds": 120,
  "fps": 30,
  "quality": "medium",
  "microphone_enabled": true,
  "microphone_device_id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}"
}
```

API request:

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
  }
}
```

Endpoint:

```http
POST /api/v1/recordings/quick
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <agent-name>
```

## Tool: get_recording_status

### Parameters

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `recording_id` | string | Yes | Recording id |

Maps to:

```http
GET /api/v1/recordings/{recording_id}
```

The response includes `elapsed_seconds`: wall-clock seconds from capture start
to now for an active recording, or to `completed_at` for a terminal recording.
It is `0` before capture starts and remains stable after termination. Agents
must not substitute `output.duration_seconds`, which is media duration reported
by ffprobe and can differ slightly from wall-clock elapsed time.

## Tool: stop_recording

### Parameters

| Parameter | Type | Required | Description |
| --- | --- | --- | --- |
| `recording_id` | string | Yes | Recording id |
| `reason` | string | No | Stop reason |

Maps to:

```http
POST /api/v1/recordings/{recording_id}/stop
```

Note: the local user can also stop recordings through the floating stop button,
tray menu, or the global `Ctrl+Shift+F10` hotkey. The agent stop API remains
available for programmatic control.

The stop response includes `status` and `stop_reason`. User-initiated stops
(`floating_button`, `tray_menu`, `global_hotkey`, `user_requested`) that produce
a valid output result in `status=completed` even when the actual duration is
shorter than planned. Only real output defects (zero duration, tiny file, or
non-zero encoder exit code) result in `status=failed`.

## Recommended Agent Flow

1. Run `AgentRecorder.Cli.exe ensure-running --json`.
2. Read the API key from `api_key_file`.
3. If `ensure_context_available=true`, keep the `ensure_context_id` and
   `ensure_context_header` for the next recording request.
4. Call `GET /api/v1/capabilities`.
5. For common requests, call `record_screen`, which maps to
   `/recordings/quick`. Include the `X-Agent-Recorder-Ensure-Context` header
   with the one-time context ID from step 3, if available.
6. If `status=requires_user_confirmation`, tell the user recording will start
   only after local confirmation.
7. Poll `/confirmations/{id}`.
8. Poll `/recordings/{id}` until `completed`, `failed`, `rejected`, or
   `expired`.
9. Report the MP4 path and relevant metadata.

The `X-Agent-Recorder-Ensure-Context` header is optional and one-time: the
server consumes the local context file when the recording intent is
authenticated and associates trusted `cold`/`warm` labels with the performance
trace. A missing, expired, malformed, identity-mismatched, or already-consumed
context does not block the recording. For concurrent or duplicate consumption
of the same ID, only one trace receives the trusted fields; the others receive
`reused` or `missing`.

`GET /api/v1/capabilities` also returns a bounded `perf_summary` with cold/warm
P50/P95 diagnostics. This summary is optional operational context for the agent;
it is not required to start a recording and never contains recording IDs, trace
IDs, output paths, API keys, or context headers.

`ensure-running` returns `ensure_context_id` and `ensure_context_header` only
when `ensure_context_available=true`. On error (`ok=false`), the JSON result
omits `startup_kind`, `ensure_elapsed_ms`, `ensure_context_id`,
`ensure_context_header`, and `ensure_context_available`. Context files and
in-memory consumption tombstones have a 5-minute TTL and a count limit so they
do not grow without bound.

For `active_window`, agents may surface `resolved_source.capture_bounds` when
diagnosing what area was actually recorded. This field is produced by Agent
Recorder after clipping the window's visible bounds to the virtual desktop.

For `selected_region`, the local user can drag a custom rectangle or click a
highlighted visible window. For `last_region`, the last successful region is
reused without opening the selection UI, but local recording confirmation is
still required.

## Safety Requirements

- The agent must never call or simulate HTTP confirmation approval.
- The agent must not claim recording has started before confirmation is
  approved.
- The agent must explain the target and duration before requesting recording.
- The local user must perform any region selection and recording confirmation.
- The API must remain bound to `127.0.0.1`.
