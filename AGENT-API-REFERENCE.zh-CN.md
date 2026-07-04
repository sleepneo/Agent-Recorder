# Agent Recorder 原始 API 快速手册

本文档给本地 AI agent 使用。录制流程由 AI agent 通过 Agent Recorder 原始 HTTP API 编排。

## 基本信息

| 项目 | 值 |
| --- | --- |
| Base URL | `http://127.0.0.1:37891/api/v1` |
| 认证 Header | `X-Agent-Recorder-Key: <api-key>` |
| Agent Header | `X-Agent-Name: <agent-name>` |
| API key 文件 | `<package-root>\.local-data\config\api-key.txt` |
| 默认视频目录 | `<package-root>\.local-data\Videos\`，前提是启动时设置 `AGENT_RECORDER_DATA_DIR` |

所有响应格式：

```json
{
  "ok": true,
  "data": {},
  "request_id": "req_xxx"
}
```

错误响应：

```json
{
  "ok": false,
  "error": {
    "code": "INVALID_ARGUMENT",
    "message": "..."
  },
  "request_id": "req_xxx"
}
```

## 1. 检查能力

```http
GET /capabilities
```

用途：确认服务已启动，并读取是否支持 `display`、`window`、`region`、嵌套录制和确认机制。

该接口不需要 API key。

返回中包含 `readiness` 字段，提供启动就绪信息：

```json
{
  "readiness": {
    "ready": true,
    "pid": 1234,
    "port": 37891,
    "api_version": "v1",
    "mode": "tray",
    "startup_elapsed_ms": 850,
    "ready_file": "...",
    "api_key_file": "...",
    "named_event": "Local\\AgentRecorderReady"
  }
}
```

`readiness` 不泄露 API key 内容，只提供文件路径。

### 就绪文件（推荐）

服务启动成功后还会写入 `<data-dir>\runtime\ready.json`，AI Agent 可以轮询该文件判断服务就绪，无需盲轮询 `/capabilities`。

## 2. 列出显示器

```http
GET /displays
X-Agent-Recorder-Key: <api-key>
```

返回：

```json
{
  "displays": [
    {
      "id": "display_1",
      "name": "Display 1",
      "bounds": { "x": 0, "y": 0, "width": 1920, "height": 1080 },
      "is_primary": true
    }
  ]
}
```

## 3. 列出窗口

```http
GET /windows?include_minimized=false&include_system_windows=false
X-Agent-Recorder-Key: <api-key>
```

用途：当用户明确要录制某个窗口时，AI agent 可以列出窗口并选择匹配项。选区录制仍是最稳妥的演示路径。

## 4. 请求用户选区

```http
POST /region-selections
Content-Type: application/json

{
  "purpose": "recording",
  "timeout_seconds": 300
}
```

成功返回：

```json
{
  "status": "selected",
  "display_id": "display_1",
  "coordinate_space": "virtual_screen",
  "bounds": {
    "x": 100,
    "y": 100,
    "width": 1200,
    "height": 800
  }
}
```

可能状态：

| status | 说明 |
| --- | --- |
| `selected` | 用户已确认选区 |
| `selection_cancelled` | 用户取消 |
| `selection_timeout` | 用户超时未选择 |
| `display_unavailable` | 当前桌面会话无法枚举显示器 |

## 5. 创建录制

```http
POST /recordings
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <agent-name>
```

### 选区录制请求体

```json
{
  "source": {
    "type": "region",
    "display_id": "display_1",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 100, "y": 100, "width": 1200, "height": 800 }
  },
  "audio": {
    "microphone": { "enabled": false }
  },
  "video": {
    "fps": 15,
    "quality": "medium"
  },
  "output": {
    "directory": "default",
    "filename_template": "recording-{datetime}"
  },
  "stop_condition": {
    "type": "duration",
    "seconds": 300
  },
  "safety": {
    "require_user_confirmation": true
  }
}
```

### 显示器录制请求体

```json
{
  "source": {
    "type": "display",
    "display_id": "display_1"
  },
  "audio": {
    "microphone": { "enabled": false }
  },
  "video": {
    "fps": 15,
    "quality": "medium"
  },
  "stop_condition": {
    "type": "duration",
    "seconds": 300
  },
  "safety": {
    "require_user_confirmation": true
  }
}
```

### 窗口录制请求体

```json
{
  "source": {
    "type": "window",
    "window_id": "window_123"
  },
  "audio": {
    "microphone": { "enabled": false }
  },
  "video": {
    "fps": 15,
    "quality": "medium"
  },
  "stop_condition": {
    "type": "duration",
    "seconds": 300
  },
  "safety": {
    "require_user_confirmation": true
  }
}
```

创建录制通常返回：

```json
{
  "status": "requires_user_confirmation",
  "confirmation_id": "confirm_xxx",
  "summary": {
    "source": "region:Display 1",
    "duration": "300s",
    "output": "..."
  }
}
```

AI agent 必须等待本地用户确认。

## 6. 查询确认状态

```http
GET /confirmations/{confirmation_id}
X-Agent-Recorder-Key: <api-key>
```

返回：

```json
{
  "confirmation_id": "confirm_xxx",
  "status": "approved",
  "recording_id": "rec_xxx"
}
```

状态：

| status | 说明 |
| --- | --- |
| `pending` | 等待用户确认 |
| `approved` | 用户已批准，返回 `recording_id` |
| `rejected` | 用户拒绝，录制未开始 |
| `expired` | 确认超时，录制未开始 |

禁止接口：

```http
POST /confirmations/{id}/approve
```

该接口会返回 405。AI agent 不得绕过本地确认。

## 7. 查询录制状态

```http
GET /recordings/{recording_id}
X-Agent-Recorder-Key: <api-key>
```

完成后返回：

```json
{
  "recording_id": "rec_xxx",
  "status": "completed",
  "elapsed_seconds": 300,
  "output": {
    "path": "...\\recording-2026-07-02-120000.mp4",
    "bytes_written": 1234567,
    "duration_seconds": 300.0,
    "width": 1200,
    "height": 800
  }
}
```

状态：

| status | 说明 |
| --- | --- |
| `pending_confirmation` | 等待确认 |
| `recording` | 正在录制 |
| `completed` | 已完成 |
| `failed` | 失败 |
| `cancelled` | 已取消 |
| `rejected` | 用户拒绝 |
| `expired` | 确认超时 |

## 8. 停止手动录制

```http
POST /recordings/{recording_id}/stop
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "reason": "user_requested"
}
```

## 9. 嵌套录制

外层录制：

```json
{
  "source": {
    "type": "display",
    "display_id": "display_1"
  },
  "audio": {
    "microphone": { "enabled": false }
  },
  "video": {
    "fps": 15,
    "quality": "medium"
  },
  "output": {
    "directory": "default",
    "filename_template": "nested-outer-{datetime}"
  },
  "stop_condition": {
    "type": "duration",
    "seconds": 300
  },
  "nested": {
    "role": "outer",
    "session_id": "nested-20260702-120000"
  },
  "safety": {
    "require_user_confirmation": true
  }
}
```

内层录制：

```json
{
  "source": {
    "type": "region",
    "display_id": "display_1",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 200, "y": 200, "width": 900, "height": 600 }
  },
  "audio": {
    "microphone": { "enabled": false }
  },
  "video": {
    "fps": 15,
    "quality": "medium"
  },
  "output": {
    "directory": "default",
    "filename_template": "nested-inner-{datetime}"
  },
  "stop_condition": {
    "type": "duration",
    "seconds": 60
  },
  "nested": {
    "role": "inner",
    "parent_recording_id": "<outer recording_id>",
    "session_id": "nested-20260702-120000"
  },
  "safety": {
    "require_user_confirmation": true
  }
}
```

限制：当前 MVP 最多 2 个并发录制，即 1 个 outer + 1 个 inner。

## 10. AI agent 推荐轮询节奏

- `/capabilities`：每 500ms 轮询，最多 30 秒。
- `/confirmations/{id}`：每 500ms 轮询，最多 120 秒。
- `/recordings/{id}`：每 1 秒轮询，直到完成或超时。

## 11. 最小使用闭环

1. AI agent 启动 `AgentRecorder.App.exe`。
2. AI agent 等待 `/capabilities` 可用。
3. AI agent 读取 API key。
4. 人类用户说：“帮我选区录屏 30 秒。”
5. AI agent 请求 `/region-selections`。
6. 人类用户框选区域。
7. AI agent 创建 `/recordings`。
8. 人类用户确认录制。
9. AI agent 轮询完成并报告 MP4 路径。
