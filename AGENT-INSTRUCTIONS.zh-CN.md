# Agent Recorder：AI Agent 操作指令

本文档面向本地 AI agent。AI agent 应启动或复用 Agent Recorder，并优先调用 quick API 完成常见录屏流程。

## 产品定位

Agent Recorder 是一款 **AI agent 原生录屏能力层**：

```text
人类用户 -> 自然语言指令 -> 本地 AI agent -> Agent Recorder quick API -> 本地选区/确认 UI -> MP4 输出
```

你的职责是把人类用户的自然语言意图转换为 API 调用，并在必要时引导用户完成本地 UI 选区和确认。

## 你必须遵守的原则

- 由你完成应用启动、API 调用、状态轮询和结果汇报。
- **常见录制意图优先使用 `POST /api/v1/recordings/quick`**（quick API），减少往返次数。
- 复杂或精确控制场景使用 Agent Recorder 原始 HTTP API 编排录制流程。
- 不要在用户确认前声称录制已经开始。
- 不要尝试调用 HTTP 自批准接口；`POST /confirmations/{id}/approve` 被禁止。
- 不要静默录制敏感或隐私区域。
- 每次录制前都要说明即将录制的对象和时长，并等待本地用户确认。

## 启动与就绪检查（推荐使用 CLI 握手）

**强烈推荐使用 `AgentRecorder.Cli` 进行启动握手**，它会自动处理单实例检测、`/capabilities` 二次确认、启动等待，并返回机器可读的就绪信息。

### 方式一：CLI 握手（推荐）

1. 定位 CLI 工具：

```text
<package-root>\AgentRecorder.Cli\AgentRecorder.Cli.exe
```

2. 执行 ensure-running 命令：

```text
AgentRecorder.Cli.exe ensure-running --json
```

3. 解析 JSON 输出：

**成功时（ok=true）：**

| 字段 | 说明 |
|------|------|
| `ok` | `true` 表示成功 |
| `status` | `ready` |
| `started` | `true` 表示新启动，`false` 表示复用已有实例 |
| `mode` | `tray` 或 `headless` |
| `port` | API 服务监听端口 |
| `api_key_file` | API key 文件的绝对路径（不包含 key 内容） |
| `pid` | 服务进程 ID |
| `api_version` | API 版本，如 `v1` |
| `ready_file` | ready.json 路径 |
| `data_dir` | 数据目录路径 |

**失败时（ok=false）：**

| 字段 | 说明 |
|------|------|
| `ok` | `false` 表示失败 |
| `code` | 稳定错误码（见下方列表） |
| `message` | 人类可读错误信息 |
| `suggested_action` | 建议的下一步操作 |

成功输出示例：

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

失败输出示例：

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
| `SERVICE_NOT_FOUND` | 找不到 AgentRecorder.App.exe 或 AgentRecorder.Headless.exe |
| `SERVICE_EXITED` | 服务进程启动后提前退出 |
| `STALE_READY_FILE` | ready 文件存在但 PID 不是 Agent Recorder 进程 |
| `CAPABILITIES_UNAVAILABLE` | PID 存活但 `/capabilities` 不可用 |
| `CAPABILITIES_IDENTITY_MISMATCH` | ready 文件与 `/capabilities` 身份字段不匹配，且已有实例持有 mutex |
| `INSTANCE_ALREADY_RUNNING_BUT_UNHEALTHY` | 有实例在运行（mutex 持有）但当前 data-dir 下不健康 |
| `STALE_READY_FILE_DELETE_FAILED` | stale ready 文件无法删除，需要人工清理后重试 |
| `INVALID_ARGUMENT` | 参数错误 |

CLI 会自动：
- 检测已有运行实例并复用
- 通过 `/api/v1/capabilities` 二次确认服务健康状态
- 如未运行则启动新实例（默认 Tray/App 模式，支持本地选区和确认 UI）
- 等待服务就绪（30秒超时）
- 返回统一格式的 JSON

**CLI 参数：**

| 参数 | 说明 | 默认值 |
|------|------|--------|
| `--json` | 输出 JSON 格式（推荐 AI agent 使用） | - |
| `--package-root <path>` | portable 包根目录 | 自动推断 |
| `--app <path>` | 指定 App exe 路径 | 自动查找 |
| `--data-dir <path>` | 数据目录 | `<package-root>\.local-data` |
| `--timeout-seconds <n>` | 等待就绪秒数 | 30 |
| `--headless` | 以 headless 模式启动（高级选项） | - |
| `--tray` | 以 tray (GUI) 模式启动 | 默认 |
| `--help` | 显示帮助 | - |

**注意：** 默认启动 Tray App 模式，它提供本地选区和确认 UI，是主产品路径。仅在确无 GUI 需求时使用 `--headless`。

### 方式二：直接启动（备选）

1. 定位发布包根目录，例如：

```text
<package-root>\
```

2. 启动应用：

```text
<package-root>\AgentRecorder.App\AgentRecorder.App.exe
```

建议以进程环境变量指定数据目录：

```text
AGENT_RECORDER_DATA_DIR=<package-root>\.local-data
```

这样 API key、审计日志和录制文件都保存在发布包本地目录下。如果直接启动 App/Headless 且不设置该环境变量，默认 data-dir 是 `%LOCALAPPDATA%\AgentRecorder`。读取 API key、ready 文件和日志路径时，以 `ready.json` 或 `/capabilities` 返回的绝对路径为准。

3. 等待服务就绪：

服务成功启动后，会在 `<data-dir>\runtime\ready.json` 原子写入 JSON 文件。AI Agent 可以：

- **轮询 ready.json**：检查文件是否出现，而非盲轮询 `/capabilities`
- **读取 ready.json**：获取 pid、port、startup_elapsed_ms、api_key_file 路径等信息
- **二次确认**：调用 `GET /api/v1/capabilities`，检查返回的 `readiness.ready` 字段

ready.json 只包含路径和状态，**不包含 API key 内容**。

如果 ready.json 不存在（如旧版本），仍可回退轮询：

```http
GET http://127.0.0.1:37891/api/v1/capabilities
```

该接口不需要 API key。

4. 读取 API key：

```text
<data-dir>\config\api-key.txt
```

如果文件暂未出现，可以先请求一次受保护接口触发生成：

```http
GET http://127.0.0.1:37891/api/v1/recordings
```

收到 401 是正常现象，然后等待 `api-key.txt` 出现。后续受保护接口都带上：

```http
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <your-agent-name>
```

## 开机自启管理（autostart）

Agent Recorder 支持当前用户级别的开机自启（登录自启），通过写入注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 实现。

### 查询自启状态

```text
AgentRecorder.Cli.exe autostart status --json
```

返回字段：

| 字段 | 说明 |
|------|------|
| `ok` | `true` 表示查询成功 |
| `status` | `enabled` / `enabled_mismatch` / `disabled` |
| `enabled` | 是否已启用自启 |
| `matches_current_app` | 自启路径是否匹配当前 App 路径 |
| `value_name` | 注册表值名称，固定为 `Agent Recorder` |
| `app_path` | 当前 App 可执行文件路径 |
| `configured_command` | 注册表中配置的启动命令（仅启用时有值） |

### 启用/禁用自启

```text
AgentRecorder.Cli.exe autostart enable --json
AgentRecorder.Cli.exe autostart disable --json
```

**何时建议用户启用自启：**

- 用户频繁使用录屏功能，且希望减少冷启动等待时间
- 用户在长会话中可能多次触发录制
- 用户明确要求"开机自动启动"

**注意事项：**

- `ensure-running` 仍是录制前的推荐入口，autostart 只是减少冷启动概率
- 启用/禁用自启需要用户明确同意，不要自动启用
- 自启仅对当前用户生效，不影响其他用户
- 不要通过 HTTP API 启用/禁用自启，只能通过 CLI 显式操作

## FFmpeg 预热

服务启动并 ready 后，会在后台低优先级预热 FFmpeg/FFprobe（执行 `-version` 检查），减少第一次录制时的启动抖动。

- 预热不阻塞 `ready.json` 写入和 API 就绪
- 预热失败不影响服务可用性，只是第一次录制可能稍慢
- 可通过 `/api/v1/capabilities` 查看预热状态：`ffmpeg.prewarm.status`

预热状态值：`not_started` | `running` | `completed` | `failed` | `skipped`

这是纯后台优化，不改变任何安全确认流程。

## Quick Recording 意图 API（推荐）

**常见录制意图请优先使用 `POST /api/v1/recordings/quick`**，它把"目标解析 + 录制创建"合并为一次 HTTP 调用，减少往返次数。

支持四种目标类型：

| `target.type` | 说明 | 适用场景 |
|---------------|------|----------|
| `primary_display` | 录主显示器 | "录整个屏幕"、"录主屏 5 分钟" |
| `active_window` | 录当前活动窗口 | "录当前窗口"、"录这个窗口 3 分钟" |
| `selected_region` | 让用户选区后录制 | "录选区"、"录这个区域 1 分钟" |
| `last_region` | 复用最近一次成功选区 | "录上次选区"、"录刚才那个区域 1 分钟" |

所有 quick 请求仍然进入本地确认流程，不能绕过用户确认。

### 快速调用模板

录主屏 5 分钟：

```json
{
  "target": { "type": "primary_display" },
  "duration_seconds": 300,
  "video": { "fps": 30, "quality": "medium" }
}
```

录当前活动窗口 3 分钟：

```json
{
  "target": { "type": "active_window" },
  "duration_seconds": 180,
  "audio": { "microphone": { "enabled": false } }
}
```

让用户选区并录制 1 分钟：

```json
{
  "target": { "type": "selected_region", "selection_timeout_seconds": 120 },
  "duration_seconds": 60
}
```

选区 UI 会覆盖整个虚拟桌面。用户可以拖拽自定义区域，也可以点击青色高亮的可见窗口直接选择其边界；创建、移动和缩放时默认吸附显示器/窗口边缘，按住 `Alt` 可临时关闭吸附。AI agent 应等待本地用户完成选区，不要把 API 请求已发出描述为录制已经开始。

复用上次选区录制 1 分钟（`last_region` 不会弹出选区窗口，直接进入本地确认）：

```json
{
  "target": { "type": "last_region" },
  "duration_seconds": 60
}
```

如果 `context.last_selected_region == null`，调用 `last_region` 会返回 `SOURCE_NOT_FOUND`，此时应改用 `selected_region` 先让用户选区。

### 响应说明

- 成功创建待确认录制：响应包含 `status: "requires_user_confirmation"` 和 `quick` 元数据（`target_type`、`recording_created: true`、`resolved_source`、`requires_user_confirmation: true`）。
- `selected_region` 被取消/超时/不可用：响应包含对应 `status`（`selection_cancelled` / `selection_timeout` / `display_unavailable` / `selection_failed`）和 `quick.recording_created: false`，此时没有创建 recording。
- `primary_display` / `active_window` / `last_region` 找不到来源：返回 `SOURCE_NOT_FOUND` 错误，附带 `suggested_action`。
- `last_region` 无上次选区：`suggested_action = "use_selected_region_first"`。

**注意**：quick API 仍然需要本地用户确认才能真正开始录制。在用户确认前，不要声称"录制已经开始"。

### 本地确认队列

多个待确认请求会进入**本地确认队列**，不会因为已有 pending confirmation 就被自动拒绝。用户操作流程：

- Agent Recorder 弹出本地确认窗体（非阻塞 modeless），显示录制信息
- 托盘菜单显示队列位置，如「确认录屏 (1/2)」「拒绝录屏 (1/2)」
- 用户明确点击「确认」批准当前队首；默认焦点在「拒绝」，按 Enter/Esc/关闭 X 会拒绝当前请求
- 用户可以在确认窗体中点击「更改...」选择本次保存目录，也可以勾选「记住为默认保存位置」
- 当前项完成后自动显示下一个待确认项

**AI agent 行为**：
- 只能等待确认状态变化，不能批准或拒绝
- 推荐使用长轮询：`GET /confirmations/{id}?wait_ms=25000&since_status=pending`
- 状态 `approved` -> 录制开始，获取 `recording_id`
- 状态 `rejected` -> 录制被拒绝，告知用户
- 状态 `expired` -> 确认超时，建议重试

复杂或需要精确控制的场景（如嵌套录制、自定义输出目录、音频配置等）仍可使用原始 `POST /api/v1/recordings`。

### 上下文快照（减少往返）

服务启动后，优先调用 `/capabilities` 获取 `context` 快照，基于以下信息决策：

```json
{
  "context": {
    "displays": {
      "available": true,
      "count": 2,
      "primary_display_id": "display_1",
      "virtual_bounds": { "x": -1920, "y": 0, "width": 3840, "height": 1080 },
      "items": [...]
    },
    "windows": {
      "available": true,
      "active": {
        "id": "window_123456",
        "title": "ChatGPT - Chrome",
        "app_name": "chrome.exe"
      },
      "items_sample": [...]
    },
    "last_selected_region": {
      "available": true,
      "bounds": { "x": 100, "y": 150, "width": 800, "height": 600 }
    }
  }
}
```

**决策逻辑：**

| 用户请求 | 条件 | 推荐 action |
|----------|------|-------------|
| "录当前窗口" | `context.windows.active != null` | 使用 `quick_recipes.record_active_window` |
| "录当前窗口" | `context.windows.active == null` | 提示用户聚焦窗口或改用 `selected_region` |
| "录主屏幕" | `context.displays.primary_display_id != null` | 使用 `quick_recipes.record_primary_display` |
| "录上次选区" | `context.last_selected_region != null` | 使用 `quick_recipes.record_last_region` |
| "录上次选区" | `context.last_selected_region == null` | 提示用户先进行选区或改用 `selected_region` |

## 场景 1：用户说"帮我录制当前对话窗口 5 分钟"

推荐用 quick API 的 `selected_region`，因为“当前对话窗口”对 AI agent 来说可能不等同于稳定窗口句柄。

1. 回复用户：

```text
好的，我会请求 Agent Recorder 进行选区录制。请在弹出的界面中框选当前对话窗口区域，随后确认录制。
```

2. 发起 quick 录制请求：

```http
POST /api/v1/recordings/quick
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <your-agent-name>

{
  "target": { "type": "selected_region", "selection_timeout_seconds": 300 },
  "duration_seconds": 300,
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
  }
}
```

3. 如果返回 `selection_cancelled`、`selection_timeout` 或 `display_unavailable`，向用户说明没有创建录制。

4. 如果返回 `requires_user_confirmation`，告诉用户：

```text
选区已确认。请在本地确认窗口或系统托盘中批准录制。录制会在你确认后开始。
```

5. 使用长轮询等待确认状态变化（推荐）：

```http
GET /api/v1/confirmations/{confirmation_id}?wait_ms=25000&since_status=pending
```

直到 `status=approved` 并取得 `recording_id`。如果状态是 `rejected` 或 `expired`，向用户说明录制没有开始。

6. 使用长轮询等待录制状态变化（推荐）：

```http
GET /api/v1/recordings/{recording_id}?wait_ms=25000&since_status=recording
```

直到 `status=completed`，然后向用户报告：

```text
录制已完成。
- 视频路径：<output.path>
- 时长：<output.duration_seconds> 秒
- 分辨率：<output.width>x<output.height>
- 文件大小：<output.bytes_written>
```

## 场景 2：用户说“选区录屏 3 分钟”

同场景 1，但 `duration_seconds` 设置为 `180`。

你可以直接说：

```text
好的，请在弹出的选区界面中框选要录制的区域。录制需要你在本地确认后才会开始。
```

## 场景 3：用户说“开始外层录制，然后在里面再录制一个窗口 1 分钟”

这是嵌套录制。外层记录整个过程，内层记录再次选择的区域。

1. 创建外层录制：

```json
{
  "target": { "type": "primary_display" },
  "duration_seconds": 300,
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
  "nested": {
    "role": "outer",
    "session_id": "nested-<timestamp>"
  }
}
```

2. 发送到 `POST /api/v1/recordings/quick`。

3. 等待用户确认外层录制，并取得外层 `recording_id`。

4. 当用户提出内层录制需求时，请求选区并创建内层录制：

```json
{
  "target": { "type": "selected_region", "selection_timeout_seconds": 120 },
  "duration_seconds": 60,
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
  "nested": {
    "role": "inner",
    "parent_recording_id": "<outer recording_id>",
    "session_id": "nested-<same timestamp>"
  }
}
```

发送到 `POST /api/v1/recordings/quick`。内层仍需本地选区和确认。

5. 等待内层和外层都完成，然后报告两段视频路径，并说明外层视频记录了内层录制的发起过程。

## 失败与拒绝处理

- `selection_cancelled`：用户取消选区，告诉用户录制未开始。
- `selection_timeout` 或 `SELECTION_TIMEOUT`：用户未及时选区，建议重试。
- `rejected`：用户拒绝确认，告诉用户录制未开始。
- `expired`：确认超时，建议重试。
- `SOURCE_NOT_FOUND`：重新调用 `/displays` 或 `/windows` 获取来源。
- `METHOD_NOT_ALLOWED`：不要尝试 HTTP 自批准，提醒用户必须本地确认。

## API 手册

更完整的端点、请求体和响应格式见：

```text
AGENT-API-REFERENCE.zh-CN.md
```
