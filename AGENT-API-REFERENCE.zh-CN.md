# Agent Recorder API 手册

本文档给本地 AI agent 使用。**常见录制意图优先使用 quick API**（`POST /recordings/quick`），复杂或精确控制场景使用原始 HTTP API 编排。

## 基本信息

| 项目 | 值 |
| --- | --- |
| Base URL | `http://127.0.0.1:37891/api/v1` |
| 认证 Header | `X-Agent-Recorder-Key: <api-key>` |
| Agent Header | `X-Agent-Name: <agent-name>` |
| API key 文件 | 由 `ensure-running` 返回的 `api_key_file` 指定 |
| portable 默认 data-dir | `<package-root>\.local-data` |
| 直接启动默认 data-dir | `%LOCALAPPDATA%\AgentRecorder` |

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
| `CAPABILITIES_IDENTITY_MISMATCH` | ready 文件与 `/capabilities` 身份字段不匹配，且已有实例持有 mutex |
| `INSTANCE_ALREADY_RUNNING_BUT_UNHEALTHY` | 有实例在运行（mutex 持有）但当前 data-dir 下不健康 |
| `STALE_READY_FILE_DELETE_FAILED` | stale ready 文件无法删除，需要人工清理后重试 |
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
    "data_dir": "...",
    "ready_file": "...",
    "api_key_file": "...",
    "audit_log_path": "...",
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

### 上下文快照（推荐）

服务启动后，优先调用 `/capabilities` 获取 `context` 快照，可以减少额外调用 `/displays`、`/windows`、`/windows/active` 的次数。

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

**context.displays 字段说明：**

| 字段 | 说明 |
|------|------|
| `available` | 是否可用 |
| `count` | 显示器数量 |
| `primary_display_id` | 主显示器 ID |
| `virtual_bounds` | 虚拟屏幕总边界 |
| `items` | 显示器列表 |
| `error` | 错误信息（不可用时） |

**context.windows 字段说明：**

| 字段 | 说明 |
|------|------|
| `available` | 是否可用 |
| `active` | 当前激活窗口 |
| `visible_count` | 可见窗口总数 |
| `items_sample` | 最多 10 个窗口样本（active 排在首位） |
| `sample_limit` | 样本上限 |
| `error` | 错误信息（不可用时） |

**context.last_selected_region 字段说明：**

| 字段 | 说明 |
|------|------|
| `available` | 是否有历史选区 |
| `display_id` | 选区所在显示器 |
| `coordinate_space` | 坐标空间（`virtual_screen`） |
| `bounds` | 选区边界 |
| `updated_at` | 更新时间 |
| `source` | 来源（`region_selection` 或 `quick_selected_region`） |

**注意**：`last_selected_region` 是持久化状态，保存在 `<data-dir>\state\last-selected-region.json`。服务重启后仍可能返回历史选区。

**agent 使用建议：**

- 启动后优先调用 `/capabilities` 获取 `context` 快照
- 对“录当前窗口”“录主屏幕”“录上次选区”这类请求，优先基于 `context` 和 `quick_recipes` 决策
- 如果 `context.windows.active == null`，应让用户聚焦窗口或改用 `selected_region`
- 如果 `context.last_selected_region == null`，不要假设存在上次选区；可改用 `selected_region` 先让用户选区
- 显示器/窗口枚举失败时，`/capabilities` 仍返回 200，错误信息在对应 `error` 字段中

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

用途：当用户明确要录制某个窗口时，AI agent 可以列出窗口并选择匹配项。常见“录当前窗口/选区录屏”请求优先使用 quick API。

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

本地选区界面覆盖完整虚拟桌面，支持拖拽创建、移动、八方向缩放、X/Y/W/H 精确输入和常用尺寸预设。鼠标悬停可见窗口时会显示青色轮廓，单击可直接选中窗口区域；选区边缘默认吸附显示器和窗口边界，按住 `Alt` 可临时关闭吸附。多显示器场景下，遮罩会可靠置于普通最大化窗口上方。

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
| `target.type` | 是 | `primary_display` / `active_window` / `selected_region` / `last_region` |
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

**`last_region`**：复用最近一次成功保存的选区，不弹出选区窗口，直接进入本地确认。

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

如果没有上次选区，返回 `SOURCE_NOT_FOUND`：

```json
{
  "ok": false,
  "error": {
    "code": "SOURCE_NOT_FOUND",
    "message": "No last selected region is available.",
    "details": {
      "suggested_action": "use_selected_region_first"
    }
  },
  "request_id": "req_xxx"
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

### 录制前 preflight 检查

`POST /recordings` 在创建 confirmation 之前会先执行一次 **before-confirmation** preflight：

- 输出目录是否可写；
- 输出磁盘剩余空间是否满足安全阈值；
- FFmpeg / FFprobe 是否可用；
- 捕获区域尺寸是否合法（正数、偶数、≥32×32、与虚拟屏幕有正面积重叠）。

如果 before-confirmation 失败，API 立即返回 400，不会创建 pending confirmation，响应包含稳定 `error.code` 与 `error.details.suggested_action`：

```json
{
  "ok": false,
  "error": {
    "code": "OUTPUT_DIRECTORY_UNWRITABLE",
    "message": "Output directory 'D:\\recordings' is not writable: ...",
    "details": {
      "suggested_action": "choose_another_output_directory",
      "stage": "before_confirmation"
    }
  },
  "request_id": "req_xxx"
}
```

用户批准之后、真正启动 FFmpeg 之前会再次执行 **before-start** preflight，复查上述项目并额外检查目标窗口/显示器是否仍然可用。如果复查失败，录制状态会变为 `failed`，`warnings` 包含 `preflight_failed: <ERROR_CODE>`，审计日志记录 `recording.preflight_failed`，本地托盘弹出错误提示。这能避免"用户已确认但窗口已关闭"导致的空录制。

常见 preflight 错误码：

| error_code | 场景 | suggested_action |
| --- | --- | --- |
| `OUTPUT_DIRECTORY_UNWRITABLE` | 输出目录无法创建或写入临时文件 | `choose_another_output_directory` |
| `INSUFFICIENT_DISK_SPACE` | 磁盘剩余空间低于安全阈值 | `free_disk_space_or_choose_another_directory` |
| `ENCODER_UNAVAILABLE` | FFmpeg 或 FFprobe 不可用 | `check_ffmpeg_files_or_reinstall_package` |
| `SOURCE_NOT_FOUND` | 目标窗口/显示器已消失 | `choose_source_again` |
| `SOURCE_UNAVAILABLE` | 目标窗口最小化、过小或移出可捕获区域 | `restore_or_move_window_then_retry` |

### 本地确认流程

当录制请求需要确认时，Agent Recorder 会弹出本地确认窗体（非阻塞 modeless 窗体），用户可以通过以下方式操作：

- **确认窗体**：显示录制信息（来源、时长、音频、输出路径、嵌套角色、录制 ID、确认 ID、超时时间）。用户需要明确点击「确认」批准；安全默认焦点在「拒绝」，按 Enter/Esc/关闭 X 会拒绝本次录制。
- **保存目录**：批准前，用户可以点击「更改...」选择本次录制的保存目录，也可以勾选「记住为默认保存位置」。AI agent 不能通过 HTTP 批准录制或远程修改确认结果。
- **托盘菜单**：右键单击托盘图标，选择「确认录屏」或「拒绝录屏」。

多个待确认请求会进入**本地确认队列**，不会因为已有 pending confirmation 就被自动拒绝。队列中的确认项按顺序处理，当前项完成后自动显示下一项。

**队列特性**：
- 托盘菜单显示队列位置，如「确认录屏 (1/2)」
- 确认窗体显示当前项信息，关闭后自动显示下一个待确认项
- 用户操作只影响当前队首，不会影响后续项

**重要**：AI agent 无法批准或拒绝录制，只能等待确认状态变化。推荐使用长轮询等待。

## 7. 查询确认状态

### 普通查询（立即返回）

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

### 长轮询（推荐，减少往返）

```http
GET /confirmations/{confirmation_id}?wait_ms=25000&since_status=pending
X-Agent-Recorder-Key: <api-key>
```

参数说明：

| 参数 | 说明 |
|------|------|
| `wait_ms` | 最大等待毫秒数（上限 25000） |
| `since_status` | 当前已知状态（大小写不敏感） |

行为规则：

- 如果当前状态不同于 `since_status`：立即返回当前状态
- 如果当前状态等于 `since_status`：等待直到状态变化或超时
- 超时后返回当前状态，不返回错误

长轮询返回：

```json
{
  "confirmation_id": "confirm_xxx",
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

新增字段说明：

| 字段 | 说明 |
|------|------|
| `wait` | 等待信息对象 |
| `wait.requested_ms` | 请求的等待毫秒数 |
| `wait.elapsed_ms` | 实际等待的毫秒数 |
| `wait.timed_out` | 是否因超时返回（`false`=立即返回或状态变化提前返回，`true`=超时） |
| `next_poll_hint_ms` | 下次轮询建议毫秒数；`null` 表示已终止无需轮询，`500` 表示仍在 pending 建议继续 |

`since_status` 比较不区分大小写。

推荐用法：

1. 优先使用长轮询 `wait_ms=25000&since_status=pending`
2. 超时后根据 `next_poll_hint_ms` 继续轮询或再次长轮询
3. 状态变为 `approved/rejected/expired` 后停止轮询

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

### 普通查询（立即返回）

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

### 长轮询（推荐，减少往返）

```http
GET /recordings/{recording_id}?wait_ms=25000&since_status=recording
X-Agent-Recorder-Key: <api-key>
```

参数说明：

| 参数 | 说明 |
|------|------|
| `wait_ms` | 最大等待毫秒数（上限 25000） |
| `since_status` | 当前已知状态（大小写不敏感） |

行为规则：

- 如果当前状态不同于 `since_status`：立即返回当前状态
- 如果当前状态等于 `since_status`：等待直到状态变化或超时
- 超时后返回当前状态，不返回错误

长轮询返回：

```json
{
  "recording_id": "rec_xxx",
  "status": "completed",
  "elapsed_seconds": 300,
  "output": {
    "path": "...\\recording-2026-07-02-120000.mp4",
    "bytes_written": 1234567,
    "duration_seconds": 300.0
  },
  "wait": {
    "requested_ms": 25000,
    "elapsed_ms": 15200,
    "timed_out": false
  },
  "next_poll_hint_ms": null
}
```

新增字段说明：

| 字段 | 说明 |
|------|------|
| `wait` | 等待信息对象 |
| `wait.requested_ms` | 请求的等待毫秒数 |
| `wait.elapsed_ms` | 实际等待的毫秒数 |
| `wait.timed_out` | 是否因超时返回（`false`=立即返回或状态变化提前返回，`true`=超时） |
| `next_poll_hint_ms` | 下次轮询建议毫秒数；`null` 表示已终止无需轮询，`1000` 表示仍在进行建议继续 |

`since_status` 比较不区分大小写。

推荐用法：

1. 根据当前状态传 `since_status=<last_status>`，`wait_ms=25000`
2. 超时后根据 `next_poll_hint_ms` 继续轮询或再次长轮询
3. 状态变为 `completed/failed/cancelled/rejected/expired` 后停止轮询

状态：

| status | 说明 |
| --- | --- |
| `pending_confirmation` | 等待确认 |
| `recording` | 正在录制 |
| `completed` | 已完成 |
| `failed` | 失败（包括 preflight 复查失败、FFmpeg 异常退出等） |
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

## 11. AI agent 推荐轮询策略

### 长轮询优先（推荐）

优先使用长轮询等待状态变化，减少 HTTP 往返：

- `/confirmations/{id}?wait_ms=25000&since_status=pending`：等待确认状态变化
- `/recordings/{id}?wait_ms=25000&since_status=<last_status>`：等待录制状态变化

超时后根据返回的 `next_poll_hint_ms` 继续长轮询。

### 短轮询备用（不推荐）

仅在无法使用长轮询时使用短轮询：

- `/capabilities`：每 500ms 轮询，最多 30 秒。
- `/confirmations/{id}`：每 500ms 轮询，最多 120 秒。
- `/recordings/{id}`：每 1 秒轮询，直到完成或超时。

## 12. 最小使用闭环

1. AI agent 启动 `AgentRecorder.App.exe`。
2. AI agent 等待 `/capabilities` 可用。
3. AI agent 读取 API key。
4. 人类用户说：“帮我选区录屏 30 秒。”
5. AI agent 请求 `/recordings/quick`，`target.type=selected_region`。
6. 人类用户框选区域。
7. 人类用户确认录制。
8. AI agent 轮询完成并报告 MP4 路径。
