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
| `target_type` | string | Yes | `primary_display`, `active_window`, or `selected_region` |
| `duration_seconds` | integer | No | Recording duration. If omitted, recording is manual-stop |
| `selection_timeout_seconds` | integer | No | Timeout for `selected_region`, default `120` |
| `fps` | integer | No | `15`, `24`, `30`, or `60`, default `30` |
| `quality` | string | No | `low`, `medium`, or `high`, default `medium` |
| `microphone_enabled` | boolean | No | Reserved for microphone capture; current builds default to false |
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
        "enum": ["primary_display", "active_window", "selected_region"]
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
        "default": false
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
  "quality": "medium"
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
      "enabled": false
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

## Recommended Agent Flow

1. Run `AgentRecorder.Cli.exe ensure-running --json`.
2. Read the API key from `api_key_file`.
3. Call `GET /api/v1/capabilities`.
4. For common requests, call `record_screen`, which maps to
   `/recordings/quick`.
5. If `status=requires_user_confirmation`, tell the user recording will start
   only after local confirmation.
6. Poll `/confirmations/{id}`.
7. Poll `/recordings/{id}` until `completed`, `failed`, `rejected`, or
   `expired`.
8. Report the MP4 path and relevant metadata.

## Safety Requirements

- The agent must never call or simulate HTTP confirmation approval.
- The agent must not claim recording has started before confirmation is
  approved.
- The agent must explain the target and duration before requesting recording.
- The local user must perform any region selection and recording confirmation.
- The API must remain bound to `127.0.0.1`.
