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
- 普通录制前要说明即将录制的对象和时长，并等待本地用户逐次确认；一次性无人值守计划必须说明目标、时间、时长和输出，并等待本地用户完成固定区域选择与有界 Lease 批准。

## 启动与就绪检查（推荐使用 CLI 握手）

**强烈推荐使用 `AgentRecorder.Cli` 进行启动握手**。它验证单实例、`/capabilities` 和托盘进程的真实活动桌面；同用户桌面直接启动/复用，同用户隔离桌面只允许使用下文的显式高级恢复设置。Recorder 不会跨 Windows 账户派发启动。

发起 CLI 前，先检查宿主是否提供面向当前用户桌面的命令执行/授权入口；若有，通过该入口运行固定的 `AgentRecorder.Cli.exe ensure-running --json`。Codex 宿主可对具体命令使用工具授权执行入口；这是宿主提供的调用方式，不是 Recorder CLI 参数，也不代表其他 agent/宿主拥有同样能力。Recorder 不会自行跳出沙盒或取得另一账户权限。若没有合适授权执行面，或当前进程 SID 与活动桌面 SID 不同，必须保持 fail-closed，不要把 API 200、`ready.json`、进程存在或 `host.supports_region_selection_ui` 当作本地 UI 可见证明，也不要指导用户每次启动或录制前运行命令。

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
| `desktop_status` | `interactive` 表示托盘进程已证明位于活动用户桌面；headless 为 `not_required` |
| `launch_path` | `direct`、`task_scheduler_interactive_token` 或 `reuse` |
| `startup_elapsed_ms` | 服务进程启动到 ready 的耗时（毫秒；warm 时为复用服务当初的启动耗时） |
| `ensure_elapsed_ms` | 本次 `ensure-running` 握手的总墙钟耗时（毫秒；同时覆盖 cold/warm） |
| `startup_kind` | `cold`（新启动）或 `warm`（复用已有服务） |
| `ensure_context_id` | 一次性上下文 ID，例如 `ensure_<32 位十六进制>`；若 `ensure_context_available=false` 则省略 |
| `ensure_context_header` | 固定为 `X-Agent-Recorder-Ensure-Context`；若上下文不可用则省略 |
| `ensure_context_available` | `true` 表示上下文文件已创建，agent 应在下一次录制请求中透传 header；`false` 表示创建失败但 ensure 仍成功，此时省略 `ensure_context_id` 与 `ensure_context_header` |

**失败时（ok=false）：**

| 字段 | 说明 |
|------|------|
| `ok` | `false` 表示失败 |
| `code` | 稳定错误码（见下方列表） |
| `message` | 人类可读错误信息 |
| `suggested_action` | 建议的下一步操作 |

失败结果不会输出 `startup_kind`、`ensure_elapsed_ms`、`ensure_context_id`、`ensure_context_header`、`ensure_context_available` 等诊断字段。

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
  "desktop_status": "interactive",
  "launch_path": "reuse",
  "data_dir": "C:\\...\\.local-data",
  "ready_file": "C:\\...\\runtime\\ready.json",
  "api_key_file": "C:\\...\\config\\api-key.txt",
  "startup_elapsed_ms": 850,
  "startup_kind": "warm",
  "ensure_elapsed_ms": 120,
  "ensure_context_id": "ensure_0123456789abcdef0123456789abcdef",
  "ensure_context_header": "X-Agent-Recorder-Ensure-Context",
  "ensure_context_available": true
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
| `TRAY_APP_NOT_FOUND` | 普通产品流找不到 AgentRecorder.App.exe |
| `SERVICE_NOT_FOUND` | 显式 headless 模式找不到 AgentRecorder.Headless.exe |
| `SERVICE_EXITED` | 服务进程启动后提前退出 |
| `STALE_READY_FILE` | ready 文件存在但 PID 不是 Agent Recorder 进程 |
| `CAPABILITIES_UNAVAILABLE` | PID 存活但 `/capabilities` 不可用 |
| `CAPABILITIES_IDENTITY_MISMATCH` | ready 文件与 `/capabilities` 身份字段不匹配，且已有实例持有 mutex |
| `INSTANCE_ALREADY_RUNNING_BUT_UNHEALTHY` | 有实例在运行（mutex 持有）但当前 data-dir 下不健康 |
| `STALE_READY_FILE_DELETE_FAILED` | stale ready 文件无法删除，需要人工清理后重试 |
| `INVALID_ARGUMENT` | 参数错误 |
| `INTERACTIVE_DESKTOP_REQUIRED` | 当前进程不在活动用户的可见桌面，且不能安全 broker |
| `DESKTOP_LOCKED` / `NO_ACTIVE_INTERACTIVE_SESSION` | 桌面已锁定或没有可用的活动用户会话 |
| `INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED` | Recorder 不跨 Windows 账户启动；应先检查宿主是否提供获准的用户桌面执行入口 |
| `INTERACTIVE_LAUNCH_NOT_ENROLLED` | 需要先完成一次性当前用户桌面注册 |
| `INTERACTIVE_LAUNCH_STALE_BINARY` / `INTERACTIVE_LAUNCH_DATA_DIR_MISMATCH` | App 文件或数据目录与信任注册不一致 |
| `TASK_DEFINITION_MISMATCH` / `TASK_NOT_FOUND` | 计划任务缺失或与固定 App 定义不一致 |
| `CONFLICTING_DATA_DIR_INSTANCE` | 同一会话中已有实例，但它不属于请求的数据目录 |

CLI 会自动：
- 检测已有运行实例并复用
- 通过 `/api/v1/capabilities` 二次确认服务健康状态
- 如未运行则在已验证的用户交互桌面直启；同用户隔离桌面仅使用已显式注册的当前用户按需任务
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

### 高级恢复：同用户隔离桌面的一次性设置

只有当宿主没有可用的用户桌面授权执行入口，且 agent 进程 SID 与活动桌面 SID **相同**、但运行在另一个桌面时，才考虑当前用户级 Task Scheduler 恢复。须由用户在已解锁、非提权的同一账户桌面中显式运行一次：

```text
AgentRecorder.Cli.exe interactive-launch setup --json --app "<AgentRecorder.App.exe 的完整路径>" --data-dir "<与 ensure-running 一致的数据目录>"
AgentRecorder.Cli.exe interactive-launch status --json
AgentRecorder.Cli.exe interactive-launch remove --json
```

此设置只针对同一 Windows 用户，不支持 `--agent-sid` 或任何跨账户派发；`--agent-sid` 和旧跨账户注册均返回明确不支持，CLI 不会自动改动旧任务。若身份不一致，改由宿主检查授权桌面执行面；没有授权入口时停止并保留失败状态。`remove` 只撤销匹配的当前用户任务，不会关闭已经运行的 App。

App 更新或路径/数据目录改变后必须先检查状态；stale 或 mismatch 时不要绕过校验。`interactive-launch diagnose` 或 `ensure-running --diagnostics --json` 是显式 opt-in，只输出进程 ID、用户 SID、session、window station/desktop 名和启动路径，不读取窗口标题、屏幕内容或 API key。

**透传 ensure-running 上下文：** 当 `ensure_context_available=true` 时，agent 应在紧接着的下一次录制创建请求中附加 header：

```http
X-Agent-Recorder-Ensure-Context: <ensure_context_id>
```

该 header 对 `POST /api/v1/recordings` 与 `POST /api/v1/recordings/quick` 均可选。服务端会一次性消费该上下文，并将可信的 `cold`/`warm` 标签、本次握手耗时 `ensure_elapsed_ms` 与服务启动耗时 `service_startup_elapsed_ms` 关联到录制 performance trace。上下文缺失、过期、身份不匹配或已消费不会阻止录制，也不会影响 API 状态码。同一 ID 并发或重复消费时，只有一个 trace 能获得可信 cold/warm 字段；其余 trace 的 `ensure_context_status` 会表现为 `reused` 或 `missing`，且不会携带可信 startup 字段。

上下文文件存储在 `<data-dir>\runtime\ensure-contexts` 下，写入时使用同目录随机临时文件并原子落位；异常路径会清理临时文件。上下文文件与进程内消费 tombstone 的默认 TTL 均为 5 分钟，且受数量上限约束，不会无限增长。原始 context ID、header 名、上下文目录不会进入 performance JSONL 或审计日志。

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
| `windows_display` | 按 Windows“标识”数字录制指定显示器 | "录 Windows 显示器 2" |
| `active_window` | 录当前活动窗口 | "录当前窗口"、"录这个窗口 3 分钟" |
| `selected_region` | 让用户选区后录制 | "录选区"、"录这个区域 1 分钟" |
| `last_region` | 复用最近一次成功选区 | "录上次选区"、"录刚才那个区域 1 分钟" |

所有 quick 请求仍然进入本地确认流程，不能绕过用户确认。

录制 Windows 显示器 N 时，优先直接调用：

```json
{
  "target": { "type": "windows_display", "windows_display_number": 2 },
  "duration_seconds": 30
}
```

其中的 `2` 必须是 Windows“设置 > 系统 > 显示 > 标识”显示的正 JSON integer，
不是 API `display_2` 的后缀。服务端从一次当前显示器快照匹配唯一可靠编号，
找不到、为 `null` 或冲突时返回 `SOURCE_NOT_FOUND`，应刷新 capabilities/displays
或请求用户消歧，不要猜测。成功后仍会弹出本地确认窗；API 不会自批准。

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
  "duration_seconds": 180
}
```

当前版本支持麦克风录制（`audio.microphone.enabled=true`），编码为 AAC。省略 `audio.microphone.device_id` 时优先自动选择唯一 CoreAudio 多媒体默认设备，否则选择唯一 active 设备；无法唯一确定时须从 `GET /api/v1/audio/devices` 获取 `id` 后显式指定。蓝牙 Hands-Free 输入由 helper 自动识别并尝试配对同一设备的渲染端点，agent 不需要手工提供 render endpoint。若选中设备被静音，请求会在选区/确认 UI 前失败（`409 AUDIO_DEVICE_MUTED`），应用不会自动取消系统静音；若设备已知为 inactive，返回 `503 AUDIO_DEVICE_NOT_AVAILABLE`；状态未知时不阻断。未静音但音量低于 10% 时确认 UI 仅显示低音量警告，不阻断录制。

系统声音是公开能力。设置 `audio.system_audio.enabled=true` 后，会在任何捕获前解析活动 render endpoint，并进入正常的选区、确认、倒计时和录制流程；未指定 `audio.system_audio.device_id` 时使用当前 Windows 多媒体默认 render endpoint，显式 ID 必须来自 `GET /api/v1/audio/devices.output_devices`。本地确认时批准的端点在本次录制中保持固定；切换 Windows 默认输出不会自动改录其他端点，切回批准端点时会执行有界同端点恢复并如实报告连续性指标。麦克风和系统声音不能在同一请求中同时启用。能力状态以 `/capabilities`、`/permissions` 和 `/audio/devices` 的实时结果为准。

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
- 对普通“创建并等待真正开始”的请求，优先在同一个创建 POST 上使用
  `?wait_for=recording&wait_ms=25000`，不要把确认长轮询和录制长轮询作为默认串行流程
- 状态 `approved` -> 用户已批准，获取 `recording_id` 并继续轮询；此时可能仍处于准备或倒计时，不能直接声称录制已经开始
- 状态 `rejected` -> 录制被拒绝，告知用户
- 状态 `expired` -> 确认超时，建议重试

用户批准后，录制可能依次经过 `preparing`（准备后端或麦克风）、`countdown`（3-2-1 倒计时，尚未采集屏幕）、`recording`（已出现可信首帧证据）、`finalizing`（保存处理）。使用创建等待时，返回 `goal_reached=true` 才表示可信首帧已出现；返回 `timed_out=true` 后再继续使用 recording 长轮询。确认和 recording GET 长轮询保留给创建等待超时后的继续观察、兼容旧客户端或需要精确编排的高级流程。

### 创建请求的可选有界等待

如果 agent 需要把“创建请求 + 等到真正开始或明确失败”合并为一次调用，可以在
`POST /api/v1/recordings` 或 `POST /api/v1/recordings/quick` 的 query 中使用：

```text
?wait_for=recording&wait_ms=25000
```

`wait_for` 只能是 `recording`；`wait_ms` 省略为 `25000`，有效范围为 `1..25000`。
`wait_ms` 单独出现、未知 `wait_for`、空值、非整数或越界值都会在来源解析、设备枚举、
选区和确认 UI 之前返回 `400 INVALID_ARGUMENT`。不需要等待时省略整个参数，沿用原有
立即创建响应。

推荐的简单 agent 流程是：先说明目标和时长，调用 quick API 并带上上述 query，不要先单独
轮询 confirmation 再轮询 recording；然后读取
顶层 `status`、`recording.status` 和 `wait`。返回 `goal_reached=true` 才能说已经出现
可信首帧并开始录制；`terminal=true` 时根据当前终态报告拒绝、过期、失败、取消或完成；
`timed_out=true` 只表示等待预算耗尽，录制状态没有被改变，应根据返回的真实状态继续使用
`GET /recordings/{recording_id}` 长轮询。批准、preparing、countdown 或 backend 已启动
都不能当作录制已开始，也不能用 API 自批准。

等待预算只从 recording 创建后计算。`selected_region` 会先等待本地选区；用户取消或选区
超时时没有 recording，不会返回假的 `wait`。该功能不会启动、停止或缩短录制；其里程碑由
`/capabilities.interaction.creation_wait.milestone` 声明为
`trusted_first_frame_or_terminal`。若等待超时，继续用 recording GET 长轮询；若要兼容旧客户端
或独立处理确认，也可保留原有的 confirmation 与 recording GET 长轮询方式。

复杂或需要精确控制的场景（如嵌套录制、自定义输出目录、精确来源与停止条件等）仍可使用原始 `POST /api/v1/recordings`。

### 停止录制

录制开始后，本地用户可通过以下方式停止：

- 点击录制区域旁的红色悬浮停止按钮（仅停止该条录制）。
- 右键托盘图标，选择「停止录制」或「停止全部录制（N）」。
- 按全局热键 `Ctrl+Shift+F10` 停止全部活动录制。

AI agent 也可以调用 API 停止指定录制：

```http
POST /api/v1/recordings/{recording_id}/stop
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "reason": "user_requested"
}
```

无论哪种方式触发停止，都应继续轮询 `/recordings/{recording_id}` 直到状态变为 `completed`、`failed` 或 `cancelled`。

终态响应会包含 `stop_reason`：`duration_reached`（自然达到计划时长）、`floating_button`/`tray_menu`/`global_hotkey`（本地控件停止）、`user_requested`（API 停止）等。用户主动停止且输出有效时，状态仍为 `completed`，不会仅因实际时长短于计划时长而判为 `failed`；但文件过小、零时长、FFmpeg 非零退出等真实产物错误仍会失败。

### 添加章节标记

当用户说“做个标记”“标记这里”或给出具体标记名称时，AI agent 应对目标录制调用：

```http
POST /api/v1/recordings/{recording_id}/marks
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "label": "重要决定"
}
```

- 只在目标状态为 `recording` 时调用；成功响应已经包含服务端接受的 `t_ms`，无需额外查询。
- 不要设置 `source: "hotkey"`；远程调用省略 `source` 即可，服务端会记为 `agent`。
- 用户也可以按 `Ctrl+Shift+F11` 本地标记当前所有活动录制。嵌套录制时，该热键会分别标记 outer 和 inner；API 调用则只标记路径中的指定 `recording_id`。
- API 返回成功后再告知用户标记已添加；`409 RECORDING_NOT_ACTIVE` 表示录制尚未开始或已经进入停止/保存阶段，不应把它误报为成功。
- 成功 FFmpeg MP4 录制的标记会写入对应 bundle 的 `marks.json`。

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

当用户说“录制显示器 N”时，优先使用上面的 `windows_display` quick target。
服务端只接受当前快照中唯一可靠的 `windows_display_number`，编号不可用或冲突时
刷新 capabilities/displays 或请求用户消歧，不要猜测目标显示器。原始 API 仍使用
从 `/displays` 返回的 opaque `id` 作为 `source.display_id`；其数字后缀不保证等于
Windows“设置 > 系统 > 显示 > 标识”的序号，也不要自行拼接 `display_N`。

| 用户请求 | 条件 | 推荐 action |
|----------|------|-------------|
| "录当前窗口" | `context.windows.active != null` | 使用 `quick_recipes.record_active_window` |
| "录当前窗口" | `context.windows.active == null` | 提示用户聚焦窗口或改用 `selected_region` |
| "录主屏幕" | `context.displays.primary_display_id != null` | 使用 `quick_recipes.record_primary_display` |
| "录上次选区" | `context.last_selected_region != null` | 使用 `quick_recipes.record_last_region` |
| "录上次选区" | `context.last_selected_region == null` | 提示用户先进行选区或改用 `selected_region` |

## 有限无人值守计划

仅当用户明确要求稍后自动录制时使用。先读取 `/capabilities.unattended_lease`；只有 `supported`、`current_enabled` 和 `execution_supported` 都为 `true` 才提交 `POST /plans`。一次性计划限制为固定区域、无音频、自然唤醒和交互桌面，单次最长 10 分钟、Lease 最长 1 小时。每日/每周周期计划还需检查 `unattended_lease.recurring` 的支持状态和有界配额。

提交时必须使用稳定 `Idempotency-Key`，并明确给出 UTC 的 `start_at`、`latest_start_at`、`planned_end_at`、Lease `expires_at`、绝对输出目录和冻结文件名。随后提示用户在本地完成重新选区与 Lease 批准，并通过 `/plan-setups/{id}` 的 `status_version_cursor` 做有界长轮询。

不要声称 API 已经批准录制；不要复用 `last_region`、活动窗口或旧授权；不要请求音频、嵌套或并发。`/plan-setups/{id}` 的 `scheduled` 只证明本地批准和排期。使用 `GET /plans/{plan_id}/status` 查询持久化的 Occurrence/Run 状态与经核验的实际媒体路径；只有可信 `recording` 才能报告已开始，只有成功结算且有产物证据才能报告已完成。拒绝、撤销、过期、错过窗口、会话不可用、目标/输出变化或重启后的不确定执行都应解释其 `reason_code`，不得自行重试或新建替代 Run。

若用户要求“到点时再由我确认”，使用一次性 `requested_authorization.mode: "required"`，并先检查 `/capabilities.required_once_plan.execution_supported`。这不是无人值守 Lease：用户需要本地选区、批准创建计划，并在到点时第二次点击本地执行确认；创建批准或 `scheduled` 都不表示已授权捕获。若第二个窗口被拒绝、过期或不可安全显示，不得改用无人值守模式或直接创建普通录制作为替代。

完整字段与示例见 `AGENT-API-REFERENCE.zh-CN.md` 的有限无人值守计划章节。

## 场景 1：用户说"帮我录制当前对话窗口 5 分钟"

推荐用 quick API 的 `selected_region`，因为“当前对话窗口”对 AI agent 来说可能不等同于稳定窗口句柄。

1. 回复用户：

```text
好的，我会请求 Agent Recorder 进行选区录制。请在弹出的界面中框选当前对话窗口区域，随后确认录制。
```

2. 发起 quick 录制请求：

```http
POST /api/v1/recordings/quick?wait_for=recording&wait_ms=25000
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <your-agent-name>

{
  "target": { "type": "selected_region", "selection_timeout_seconds": 300 },
  "duration_seconds": 300,
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

4. 如果返回带 `wait` 的响应，读取 `status`、`recording.status` 和 `wait`：

```text
只有 `goal_reached=true` 才表示录制已出现可信首帧并开始；`terminal=true` 按终态说明没有开始或已经结束；`timed_out=true` 则继续使用 recording 长轮询。
```

5. 仅在创建等待超时、兼容旧客户端或需要高级编排时，才单独长轮询确认状态：

```http
GET /api/v1/confirmations/{confirmation_id}?wait_ms=25000&since_status=pending
```

直到 `status=approved` 并取得 `recording_id`。如果状态是 `rejected` 或 `expired`，向用户说明录制没有开始。

6. 取得 `recording_id` 后，使用长轮询等待录制状态变化：

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

## 窗口目标的确认说明

向用户描述窗口录制时，必须使用确认摘要中的 `capture_semantics` 和
`preview_semantics`：`window_surface` 是所选窗口内容且排除遮挡窗口；
`screen_rectangle` 是窗口当前屏幕区域，遮挡窗口可能被录入。不要把通用的
`source_type=window` 当成窗口表面承诺，也不要把屏幕截图称为窗口内容预览。

如果批准后的计划校验返回 `capture_semantics_changed`，应报告“捕获方式在确认后
发生变化，录制未开始”，然后重新创建请求。不得通过 HTTP 自批准，也不得把该失败
当作已开始录制或返回一个输出文件路径。

严格窗口及未来窗口单次执行的 `storage_space_low` 表示实际输出/暂存卷容量不足，
`storage_capacity_unavailable` 表示容量无法可信确认（含卷不可用、身份改变和查询超时）。
运行中可信中止为 failed，原因在 `stop_reason`；不得将 partial 当作最终视频。
启动容量估计不是预留，运行低水位固定 256 MiB。没有面向 Agent 的关闭监控、
更换监控盘或阈值开关；失败授权不可自动复用，后续新录制仍遵循本地批准流程。

### 配置每条录制的开始前倒计时

raw `POST /api/v1/recordings` 和 quick `POST /api/v1/recordings/quick` 使用同一个顶层
字段 `countdown_seconds`。省略为 3；有效范围是整数 `0..10`。传入负数、超过 10、
浮点数、字符串、布尔值、`null`、对象或数组会在目标解析/选区 UI 之前返回
`400 INVALID_ARGUMENT`。`0` 只关闭可见倒计时，不会跳过确认、准备、预检、捕获授权或
可信首帧门槛。请从 `/capabilities.interaction.countdown` 读取能力范围和默认值，并在
`/recordings/{id}` 的 `config.countdown_seconds` 中确认最终规范化值。

自然语言映射示例：用户说“倒计时 5 秒后录屏”时发送
`countdown_seconds: 5`；用户说“立即开始”时发送 `countdown_seconds: 0`，但仍等待
本地确认、准备和可信首帧，不得把 API 返回的 `pending_confirmation`、`preparing` 或
`countdown` 状态描述为已经开始录制。

### 有界截图序列

当用户明确要求“定时截图/截图序列”时，使用 quick API 的同一顶层契约：
`mode: "screenshot_series"`、`interval_ms: 1000..3600000`，以及二选一的
`max_count: 1..300` 或 `max_duration_seconds: 1..86400`。不要同时发送两个边界，
也不要为截图序列发送任何音频字段。raw 请求不得发送 `stop_condition`，quick 请求
不得发送 `duration_seconds` 或 `stop_condition`；服务会在目标解析和音频设备枚举前以
`400 INVALID_ARGUMENT` 拒绝这些冲突字段。不要把视频 duration 映射到截图序列，时长
边界只使用 `max_duration_seconds`。

轮询 recording 状态时，把 `pending_confirmation`、`preparing`、`countdown`、
`capturing`、`completed`、`cancelled`、`failed` 分开报告。完成后报告 `output.path`
目录和 `series.json`，并说明这是 PNG 文件夹，不是 MP4；有帧主动停止会发布 partial
目录，零帧停止不会发布空目录。`max_duration_seconds` 从第一张有效 PNG 原子提交
时开始计时（第一张为 `t=0`）；到达 deadline 后是正常 `completed`，可以出现计划数
大于实际数。截图序列不使用章节标记热键或 marks API；确认、runner 和 manifest 的
坐标空间都应为 `virtual_screen`。
每个计划点从第一个有效 PNG 提交锚定的 scheduled start 认领，随后只启动一个有限的
FFmpeg 单帧进程；不要把截图序列理解成连续 worker，也不要并发追赶。完成的
`series.json` 中，`captured_offset_ms` 是有效提交相对首帧锚点的偏移，`lateness_ms` 是
不含本帧捕获/编码耗时的认领迟到，`capture_duration_ms` 是认领到有效 PNG 提交的单调
时钟耗时。不要向用户承诺固定的桌面毫秒延迟。

### 未来程序窗口的一次性授权

仅在 `/capabilities.future_window_one_shot.setup_supported=true` 且
`execution_supported=true` 时使用 `POST /api/v1/future-window-authorizations`。
请求只创建幂等 `pending` 设置，不会启动播放器、读取窗口内容或开始录制；用户必须在
交互桌面检查完整授权范围并点击本地批准。相同 `Idempotency-Key` 和相同规范化范围重放
返回原设置，不同范围返回冲突。仅支持本地 `.exe`、1–1800 秒、最多 3600 秒有效期、固定
输出目录，以及明确的 `audio.mode=none` 或精确 `system_loopback` endpoint。

本地批准后，agent 自行安排何时启动内容，并从本机窗口清单取得唯一精确 `window_id`，再
调用 `POST /api/v1/future-window-authorizations/{authorization_id}/runs`。Recorder 只接受
批准可执行文件所属的唯一可见、未最小化顶层窗口；不按标题、前台窗口、进程名或内容猜测，
也不回退到屏幕矩形。一个授权最多启动一次。响应的 `run_id` 可用
`GET /api/v1/recordings/{run_id}` 轮询；授权状态和输出证据用授权 GET 路由读取。失败或重启
后 `started_unknown` 都不可自动重试。系统 loopback 会录下所选 render endpoint 的全部
声音，不仅是该窗口的声音。

用户可在本地「无人值守安全控制中心」查看并撤销授权；经认证的 agent 也可调用该授权的
`/revoke` 路由。撤销会阻止新启动并请求停止该授权的活动录制，不会把它报告成达到时长
上限的正常完成。此功能不表示支持 livestream 检测/选择、浏览器标签身份、自动打开播放器、
定时/重复录制、机器唤醒、麦克风或屏幕矩形回退。

## 固定区域 Profile 配置复用

### 普通交互录制

当 `/capabilities.profile_management.ordinary_recording_profile_ref_supported=true` 时，先读取 Profile 的精确 `id/version/digest`，再以唯一请求体调用 `POST /api/v1/recordings`：

```json
{"profile_ref":{"id":"profile-id","version":1,"digest":"exact-version-digest"}}
```

不可附加 raw source/output/audio、Plan/Lease/proof 或其他覆盖字段；不使用名称或 latest。此路径仍要求本地用户逐次确认，不是无人值守授权。只支持无音频固定区域和 1–600 整秒；不可精确表达的毫秒时长、奇数/过小区域、显示器或后端变化会拒绝，不会自动改配置或回退。确认窗允许用户仅为本次录制更改保存目录。POST 没有幂等键；若响应不确定，先查询录制状态，不要盲目重发。`plan_profile_ref_supported=false` 时不得把计划创建请求改成 profile ref。

历史版本列表与精确版本接口的目录元数据、当前引用和版本数据来自同一个数据库读取快照；读取规格用 canonical 策略码，并以 duration_seconds 精确返回秒值、duration_ms 返回毫秒整数。整秒输出保持整数，POST/PATCH 仍只允许 1–600 整数秒。

历史版本列表与精确版本接口的目录元数据、当前引用和版本数据来自同一个数据库读取快照；读取规格用 canonical 策略码，并以 duration_seconds 精确返回秒值，duration_ms 返回毫秒整数。整秒输出保持整数，POST/PATCH 仍只允许 1–600 整数秒。

用户要求保存或复用既有固定区域配置时，可使用 `GET /api/v1/profiles` 分页发现旧配置，再以详情给出的精确 `{id, version, digest}` 调用 `POST /api/v1/profiles` 复制。创建请求必须带新的稳定 `Idempotency-Key`；同键只能用于完全相同请求。修改使用 `PATCH` 并传详情返回的强 `ETag` 到 `If-Match`；并发或陈旧时先 GET 核对，不能自动以新 ETag 重试。DELETE 也需要 `If-Match`，被旧计划任一历史版本引用时会得到 `409 PROFILE_IN_USE`，不要尝试取消计划来绕过。

首次创建而尚无 Profile 时，若 `/capabilities.profile_management.creation_modes` 包含 `create_from_local_selection`，先调用 `POST /api/v1/regions/select` 并明确传 `{"purpose":"profile"}`，等待用户在本地选择器中确认；随后把响应中的 `selection_ref` 原样用于唯一 `POST /api/v1/profiles` 请求，提交命名和 `changes.duration_seconds`（1–600），并可选择 countdown（0–10，默认 3）、filename_prefix（默认 `recording`）及 output_directory（省略则冻结当前默认目录）。使用新的 `Idempotency-Key`。选区引用是短期配置输入，不是授权或录制凭证；取消/超时须重新选择。不可用过期引用、last-region、普通录制选择或自行构造的几何替代真实本地交互。创建之后仍须按精确 Profile ref 发起普通录制并由本地用户逐次确认；不得把配置保存当作批准或启动。

这组接口只管理固定区域、无音频的配置，不是录制授权或执行入口。不得提交选择几何、proof、批准、音频/窗口目标，也不得声称保存/复制/修改成功就代表显示器当前可用、计划已绑定或录制已批准。历史版本通过 exact version 路由读取；墓碑配置默认不在列表中，可显式请求 `include_deleted=true` 查看。
