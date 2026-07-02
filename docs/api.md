# Agent Recorder API 文档

## 基础信息

- **基础 URL**: `http://127.0.0.1:37891`
- **API 前缀**: `/api/v1`
- **认证**: 部分接口需要 `X-Agent-Recorder-Key` header

## 认证要求

| 接口 | 是否需要认证 |
|------|-------------|
| GET /capabilities | 否 |
| GET /permissions | 否 |
| GET /displays | 否 |
| GET /windows | 否 |
| GET /windows/active | 否 |
| GET /audio/devices | 否 |
| POST /recordings | 是 |
| GET /recordings | 是 |
| GET /recordings/{id} | 是 |
| POST /recordings/{id}/stop | 是 |
| GET /confirmations/{id} | 是 |

## 通用响应格式

所有响应都包含 `ok` 字段表示成功状态：

```json
{
  "ok": true,
  "data": { /* 响应数据 */ },
  "request_id": "req_abc123"
}
```

错误响应：

```json
{
  "ok": false,
  "error": "ERROR_CODE",
  "message": "错误描述",
  "details": { /* 可选详情 */ },
  "request_id": "req_abc123"
}
```

## 接口列表

### 1. GET /capabilities

获取服务能力信息。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "app": {
      "name": "Agent Recorder",
      "version": "0.1.0",
      "platform": "windows"
    },
    "recording": {
      "sources": ["display", "window"],
      "audio": ["microphone"],
      "containers": ["mp4"],
      "codecs": ["h264"],
      "fps": [15, 24, 30, 60],
      "stop_conditions": ["duration", "manual"],
      "max_duration_seconds": 7200,
      "pause_resume": false
    },
    "safety": {
      "requires_confirmation": true,
      "recording_indicator": true,
      "audit_log": true
    },
    "auth": {
      "required": true,
      "header": "X-Agent-Recorder-Key"
    }
  }
}
```

### 2. GET /permissions

获取权限状态。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "screen_capture": { "status": "granted" },
    "microphone": { "status": "granted" },
    "output_directory": { 
      "status": "granted", 
      "default_path": "D:\\works\\python\\007-Agent-Recorder\\.local-data\\Videos" 
    }
  }
}
```

### 3. GET /displays

列出可用显示器。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "displays": [
      {
        "id": "display_0",
        "name": "Primary",
        "bounds": { "x": 0, "y": 0, "width": 3840, "height": 2160 },
        "is_primary": true
      }
    ]
  }
}
```

### 4. GET /windows

列出窗口。

**查询参数**：
- `include_minimized` (bool): 是否包含最小化窗口，默认 false
- `include_system_windows` (bool): 是否包含系统窗口，默认 false

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "windows": [
      {
        "id": "window_123",
        "title": "Visual Studio Code",
        "process_name": "Code",
        "bounds": { "x": 100, "y": 100, "width": 1920, "height": 1080 },
        "is_minimized": false
      }
    ]
  }
}
```

### 5. GET /windows/active

获取当前活动窗口。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "window": {
      "id": "window_456",
      "title": "PowerShell",
      "process_name": "powershell",
      "bounds": { "x": 0, "y": 0, "width": 800, "height": 600 },
      "is_minimized": false
    }
  }
}
```

### 6. GET /audio/devices

列出音频输入设备。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "input_devices": [
      { "id": "mic_0", "name": "Microphone (Realtek High Definition Audio)" }
    ],
    "system_audio_supported": false
  }
}
```

### 7. POST /recordings

发起录制请求。需要认证。

**请求体**：

```json
{
  "source": {
    "type": "display",
    "display_id": "display_0"
  },
  "audio": {
    "microphone": {
      "enabled": false
    }
  },
  "video": {
    "fps": 30,
    "quality": "medium"
  },
  "stop_condition": {
    "type": "duration",
    "seconds": 5
  }
}
```

**请求字段说明**：

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| source.type | string | 是 | `display` 或 `window` |
| source.display_id | string | 否 | 显示器 ID（type=display 时必填） |
| source.window_id | string | 否 | 窗口 ID（type=window 时必填） |
| audio.microphone.enabled | bool | 是 | 是否启用麦克风 |
| video.fps | int | 否 | 帧率，默认 30 |
| video.quality | string | 否 | `low`, `medium`, `high`，默认 `medium` |
| stop_condition.type | string | 是 | `duration` 或 `manual` |
| stop_condition.seconds | int | 否 | 录制时长（type=duration 时必填） |

**响应示例（需要用户确认）**：

```json
{
  "ok": true,
  "data": {
    "status": "requires_user_confirmation",
    "confirmation_id": "confirm_abc123",
    "summary": {
      "source": "Display: Primary (3840x2160)",
      "duration": "5 seconds",
      "audio": "microphone disabled"
    }
  }
}
```

**响应示例（已确认，直接开始）**：

```json
{
  "ok": true,
  "data": {
    "recording_id": "rec_xyz789",
    "status": "recording",
    "started_at": "2026-06-18T08:00:00Z",
    "expected_output": "D:\\...\\recording-2026-06-18-080000.mp4"
  }
}
```

### 8. GET /recordings

列出所有录制记录。需要认证。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "recordings": [
      {
        "recording_id": "rec_xyz789",
        "status": "completed",
        "started_at": "2026-06-18T08:00:00Z",
        "completed_at": "2026-06-18T08:00:05Z",
        "output_path": "D:\\...\\recording-2026-06-18-080000.mp4"
      }
    ]
  }
}
```

### 9. GET /recordings/{id}

获取单个录制状态。需要认证。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "recording_id": "rec_xyz789",
    "status": "completed",
    "source": { "type": "display", "title": "Primary" },
    "started_at": "2026-06-18T08:00:00Z",
    "completed_at": "2026-06-18T08:00:05Z",
    "elapsed_seconds": 5,
    "audio": { "microphone": { "enabled": false } },
    "output": {
      "path": "D:\\...\\recording-2026-06-18-080000.mp4",
      "bytes_written": 133446,
      "duration_seconds": 5.034,
      "ffmpeg_exit_code": 0
    },
    "warnings": [],
    "stderr_excerpt": ""
  }
}
```

**状态说明**：

| 状态 | 说明 |
|------|------|
| `pending_confirmation` | 等待用户确认 |
| `recording` | 录制中 |
| `stopping` | 正在停止 |
| `completed` | 完成 |
| `failed` | 失败 |
| `cancelled` | 已取消 |
| `rejected` | 用户拒绝 |
| `expired` | 确认超时 |

### 10. POST /recordings/{id}/stop

停止录制。需要认证。

**请求体（可选）**：

```json
{
  "reason": "user_requested"
}
```

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "recording_id": "rec_xyz789",
    "status": "completed",
    "output": {
      "path": "D:\\...\\recording-2026-06-18-080000.mp4",
      "size_bytes": 133446,
      "duration_seconds": 5.034,
      "container": "mp4",
      "codec": "h264"
    }
  }
}
```

### 11. GET /confirmations/{id}

查询确认状态。需要认证。

**响应示例**：

```json
{
  "ok": true,
  "data": {
    "confirmation_id": "confirm_abc123",
    "status": "approved",
    "recording_id": "rec_xyz789"
  }
}
```

**确认状态说明**：

| 状态 | 说明 |
|------|------|
| `pending` | 等待用户确认 |
| `approved` | 用户已批准 |
| `rejected` | 用户已拒绝 |
| `expired` | 确认超时 |

## 错误码

| 错误码 | HTTP 状态 | 说明 |
|--------|-----------|------|
| `UNAUTHORIZED` | 401 | 缺少 API Key |
| `FORBIDDEN` | 403 | API Key 无效 |
| `SOURCE_UNAVAILABLE` | 403 | 窗口被安全策略阻止 |
| `PERMISSION_DENIED` | 403 | 路径被安全策略阻止 |
| `RECORDING_ALREADY_RUNNING` | 409 | 已有录制在进行中 |
| `RECORDING_NOT_FOUND` | 404 | 录制或确认不存在 |
| `INVALID_ARGUMENT` | 400 | 参数无效 |
| `OUTPUT_PATH_INVALID` | 400 | 输出路径无效 |
| `INTERNAL_ERROR` | 500 | 服务器内部错误 |

## 使用示例

### curl 示例

```bash
# 获取能力
curl http://127.0.0.1:37891/api/v1/capabilities

# 获取显示器列表
curl http://127.0.0.1:37891/api/v1/displays

# 发起录制（需要 API Key）
curl -X POST http://127.0.0.1:37891/api/v1/recordings \
  -H "X-Agent-Recorder-Key: your-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "source": { "type": "display", "display_id": "display_0" },
    "audio": { "microphone": { "enabled": false } },
    "video": { "fps": 30, "quality": "medium" },
    "stop_condition": { "type": "duration", "seconds": 5 }
  }'

# 查询录制状态
curl -H "X-Agent-Recorder-Key: your-api-key" \
  http://127.0.0.1:37891/api/v1/recordings/rec_xyz789
```
