# Agent Recorder API 手册

本文档给本地 AI agent 使用。**常见录制意图优先使用 quick API**（`POST /recordings/quick`），复杂或精确控制场景使用原始 HTTP API 编排。

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

## CLI 工具（推荐启动方式）

Agent Recorder 提供 `AgentRecorder.Cli` 命令行工具，用于可靠地启动或复用服务实例，并获取就绪信息。CLI 仅负责启动接管，不涉及录制流程。

### ensure-running 命令

```text
AgentRecorder.Cli.exe ensure-running [options]
```

**选项：**

| 选项 | 说明 | 默认值 |
|------|------|--------|
| `--json` | 输出 JSON 格式（推荐 AI agent 使用） | - |
| `--package-root <path>` | portable 包根目录 | 自动推断 |
| `--app <path>` | 指定 App exe 路径 | 自动查找 |
| `--data-dir <path>` | 数据目录 | `<package-root>\.local-data` |
| `--timeout-seconds <n>` | 等待就绪的最大秒数 | `30` |
| `--timeout-ms <ms>` | 等待就绪的最大毫秒数（兼容） | - |
| `--headless` | 以 headless 模式启动（高级选项） | - |
| `--tray` | 以 tray (GUI) 模式启动 | 默认 |
| `--verbose` | 输出人类可读诊断信息 | - |
| `--help, -h` | 显示帮助 | - |

**成功输出（ok=true, status=ready）：**

```json
{
  "ok": true,
  "status": "ready",
  "started": false,
  "pid": 12345,
  "port": 37891,
  "api_version": "v1",
  "mode": "tray",
  "data_dir": "C:\\...\\.local-data",
  "ready_file": "C:\\...\\runtime\\ready.json",
  "api_key_file": "C:\\...\\config\\api-key.txt",
  "startup_elapsed_ms": 850
}
```

**失败输出（ok=false）：**

```json
{
  "ok": false,
  "code": "READY_TIMEOUT",
  "message": "Agent Recorder did not become ready within 30 seconds.",
  "suggested_action": "Check whether AgentRecorder.App.exe can start in the current desktop session."
}
```

**稳定错误码：**

| 错误码 | 说明 |
|--------|------|
| `READY_TIMEOUT` | 服务在超时时间内未就绪 |
| `SERVICE_NOT_FOUND` | 找不到 AgentRecorder.App.exe 或 Headless.exe |
| `SERVICE_EXITED` | 服务进程启动后提前退出 |
| `STALE_READY_FILE` | ready 文件存在但 PID 不是 Agent Recorder 进程 |
| `CAPABILITIES_UNAVAILABLE` | PID 存活但 `/capabilities` 不可用 |
| `INSTANCE_ALREADY_RUNNING_BUT_UNHEALTHY` | 有实例在运行（mutex 持有）但当前 data-dir 下不健康 |
| `INVALID_ARGUMENT` | 参数错误 |

**注意事项：**
- `api_key_file` 字段仅提供文件路径，不包含 API key 内容
- `started` 为 `false` 表示复用已有实例，`true` 表示新启动
- CLI 自动处理单实例检测，不会启动多个服务
- CLI 通过 `/api/v1/capabilities` 二次确认服务健康，不接受仅凭 PID 的 ready 文件
- 默认启动 Tray App 模式（支持本地选区和确认 UI），仅显式 `--headless` 时使用 headless 模式

### autostart 命令

```text
AgentRecorder.Cli.exe autostart <status|enable|disable> [options]
```

管理当前用户的开机自启设置（写入/读取 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`）。

**子命令：**

| 子命令 | 说明 |
|--------|------|
| `status` | 查询当前自启状态（只读，不修改注册表） |
| `enable` | 启用开机自启 |
| `disable` | 禁用开机自启 |

**选项：**

| 选项 | 说明 | 默认值 |
|------|------|--------|
| `--json` | 输出 JSON 格式（推荐 AI agent 使用） | - |
| `--app <path>` | 指定 App exe 路径 | 自动查找 |
| `--help, -h` | 显示帮助 | - |

**status 输出示例：**

```json
{
  "ok": true,
  "status": "disabled",
  "enabled": false,
  "matches_current_app": false,
  "value_name": "Agent Recorder",
  "run_key": "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
  "app_path": "C:\\...\\AgentRecorder.App.exe",
  "code": "disabled",
  "message": "Autostart is disabled."
}
```

**status 状态值：**

| 状态 | 说明 |
|------|------|
| `enabled` | 自启已启用，且路径匹配当前 App |
| `enabled_mismatch` | 自启已启用，但路径指向旧位置或其他位置 |
| `disabled` | 自启未启用 |
| `error` | 查询/操作失败 |

**注意事项：**
- 仅对当前用户生效，不影响系统级或其他用户
- `enable`/`disable` 必须显式调用才修改注册表，不会在应用启动或 `ensure-running` 中自动启用
- 只能通过 CLI 操作，HTTP API 仅暴露状态，不提供启用/禁用能力
- 不要在未经用户同意的情况下启用自启

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

返回中还包含 `host.autostart` 字段，提供自启状态：

```json
{
  "host": {
    "autostart": {
      "supported": true,
      "enabled": false,
      "matches_current_app": false,
      "value_name": "Agent Recorder"
    }
  }
}
```

返回中包含 `ffmpeg` 字段，提供 FFmpeg 解析和预热状态：

```json
{
  "ffmpeg": {
    "resolved": true,
    "source": "project_tools",
    "prewarm": {
      "status": "completed",
      "elapsed_ms": 250
    }
  }
}
```

预热状态值：`not_started` | `running` | `completed` | `failed` | `skipped`

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

## 5. Quick Recording 意图 API（推荐）

```http
POST /recordings/quick
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <agent-name>
```

把"目标解析 + 录制创建"合并为一次 HTTP 调用，减少 agent 往返。仍然进入本地确认流程，不能绕过用户确认。

### 请求体

```json
{
  "target": {
    "type": "selected_region",
    "selection_timeout_seconds": 120
  },
  "duration_seconds": 180,
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
  },
  "nested": {
    "role": "inner",
    "parent_recording_id": "rec_xxx",
    "session_id": "session_xxx"
  }
}
```

字段说明：

| 字段 | 必填 | 说明 |
|------|------|------|
| `target.type` | 是 | `primary_display` / `active_window` / `selected_region` |
| `target.selection_timeout_seconds` | 否 | 仅 `selected_region` 生效，默认 `120`，范围 `10..600` |
| `duration_seconds` | 否 | 转换为 `stop_condition: { type: "duration", seconds: n }`；不填则手动停止 |
| `video` | 否 | 透传到原始录制配置，默认值同原始 API |
| `audio` | 否 | 透传到原始录制配置 |
| `output` | 否 | 透传到原始录制配置 |
| `nested` | 否 | 透传到原始录制配置，使用现有 nested 规则 |

### 三种目标类型

**`primary_display`**：自动选择主显示器（`is_primary=true`），没有 primary 则选第一个。

内部生成：
```json
{ "source": { "type": "display", "display_id": "display_1" } }
```

**`active_window`**：自动选择当前活动窗口。

内部生成：
```json
{ "source": { "type": "window", "window_id": "window_123" } }
```

窗口 denylist、最小化窗口检查等安全校验继续交给现有策略。

**`selected_region`**：弹出本地选区窗口，用户拖拽选择后创建录制。

内部生成：
```json
{
  "source": {
    "type": "region",
    "display_id": "display_1",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 100, "y": 100, "width": 800, "height": 600 }
  }
}
```

选区未成功（取消/超时/不可用/失败）时，不创建 recording，返回业务状态：

```json
{
  "ok": true,
  "data": {
    "status": "selection_cancelled",
    "quick": {
      "target_type": "selected_region",
      "recording_created": false
    }
  },
  "request_id": "req_xxx"
}
```

可能状态：`selection_cancelled` / `selection_timeout` / `display_unavailable` / `selection_failed`。

### 成功响应

创建录制成功后，响应包含原始 `CreateRecording` 的所有字段，并额外包含 `quick` 元数据：

```json
{
  "ok": true,
  "data": {
    "status": "requires_user_confirmation",
    "confirmation_id": "conf_xxx",
    "summary": {
      "source": "region: Display 1",
      "audio": "No audio",
      "duration": "180s",
      "output": "D:\\...\\recording.mp4",
      "nested_role": "none"
    },
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
  },
  "request_id": "req_xxx"
}
```

### 错误响应

找不到来源时返回 `SOURCE_NOT_FOUND`：

```json
{
  "ok": false,
  "error": {
    "code": "SOURCE_NOT_FOUND",
    "message": "No display is available for quick recording.",
    "details": {
      "suggested_action": "use_selected_region_or_check_desktop_session"
    }
  },
  "request_id": "req_xxx"
}
```

`active_window` 无活动窗口：

```json
{
  "ok": false,
  "error": {
    "code": "SOURCE_NOT_FOUND",
    "message": "No active recordable window is available.",
    "details": {
      "suggested_action": "ask_user_to_focus_a_window_or_use_selected_region"
    }
  },
  "request_id": "req_xxx"
}
```

## 6. 创建录制（原始 API）

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

## 7. 查询确认状态

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

## 8. 查询录制状态

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

## 9. 停止手动录制

```http
POST /recordings/{recording_id}/stop
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "reason": "user_requested"
}
```

## 10. 嵌套录制

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

## 11. AI agent 推荐轮询节奏

- `/capabilities`：每 500ms 轮询，最多 30 秒。
- `/confirmations/{id}`：每 500ms 轮询，最多 120 秒。
- `/recordings/{id}`：每 1 秒轮询，直到完成或超时。

## 12. 最小使用闭环

1. AI agent 启动 `AgentRecorder.App.exe`。
2. AI agent 等待 `/capabilities` 可用。
3. AI agent 读取 API key。
4. 人类用户说：“帮我选区录屏 30 秒。”
5. AI agent 请求 `/region-selections`。
6. 人类用户框选区域。
7. AI agent 创建 `/recordings`。
8. 人类用户确认录制。
9. AI agent 轮询完成并报告 MP4 路径。
