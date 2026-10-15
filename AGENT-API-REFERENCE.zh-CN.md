# Agent Recorder API 手册

普通、非nested视频的原始 `POST /api/v1/recordings` 和quick `POST /api/v1/recordings/quick` 支持 `"artifacts":{"bundle":false}`，用于用户只要视频、不需要说明包的请求。省略artifacts或bundle默认true，显式true继续完整五附件Bundle。政策在创建时冻结，`summary.artifacts.bundle`、视频 `config.artifacts.bundle` 与实际一致，本地改保存目录也不重置。中英确认窗显示“仅MP4”或“MP4与录制说明包”，不改变捕获范围、输出或本地授权Proof。

artifacts必须object，内部仅可选JSON boolean bundle；null、错误类型、未知/重复内部字段及重复artifacts，在设备/来源探测、选区/确认和文件操作之前400 INVALID_ARGUMENT。Profile执行/持久配置、Plan/Lease/未来窗口、截图序列、nested不支持本字段，即使true也拒绝。`capabilities.recording_artifacts` 公布模式、default_bundle=true、两个入口和ordinary_non_nested_video范围。

false不卸载App生产generator，只为当前ordinary视频跳过Bundle准备/生成，不创建operation、.bundle或截图附件。主MP4仍正式校验、原句柄发布、结算赢家并持久登记，completed事务明确保存bundle.status=not_applicable。主媒体摘要依赖原Bundle生成阶段，此时media_hash为未知/null，不补造。默认完整Bundle及generating/ready/failed/interrupted保护保持；schema仍V44，不改旧媒体、不回填历史。

成功仅MP4成片只有原锚点/大小、固定Videos范围与现有限定用途保护都通过时，才可用录制ID申请独立删除；政策本身不是删除授权。运行期主输出登记只在已知生产后端实际收尾后退休，活资源/未知继续保护。默认Bundle录制仍受保护。本源码/合成输入正式链及一次5秒无声FFmpeg选区的本地取消/批准删除与同键重放已独立验收，不代表全部后端或完整保留执行。

本文描述v0.1.16 合同（SQLite v44），包含历史、健康、产物元数据检查、生成阶段持久媒体及附件摘要、显式主视频和五附件内容核验、计划和未来窗口成片原始身份、只读保留预览，以及本机确认的单项删除申请。后者及显式仅MP4正式发布路径已通过独立源码/自动化与上述限定桌面验收，默认App录制仍生成受保护Bundle。调用前读取当前实例 `/capabilities`；主视频与附件核验共享宿主单 worker，单项删除使用文末独立本机授权合同。

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

## 启动与桌面执行边界

调用 `AgentRecorder.Cli.exe ensure-running --json` 前，agent 应先检查宿主是否提供适当的用户桌面命令执行/授权入口；若有，经该入口运行固定 CLI 命令。部分 agent 宿主可对具体命令提供工具授权执行入口，这是宿主调用方式，不是 Recorder CLI 参数，也不代表其他 agent/宿主拥有同样能力。Recorder 不会自行跳出沙盒或跨 Windows 账户派发；没有合适授权桌面执行面时必须 fail-closed。旧 `--agent-sid` 参数和跨账户注册返回 `INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED`，不自动修改旧任务。

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

## 按需只读健康快照

出现环境诊断或失败排查需求时，agent 直接携带 `X-Agent-Recorder-Key` 调用 `GET /api/v1/health`，入口见 `capabilities.health`。App 和 Headless 均支持，无须人类打开面板或转述技术状态。保留 `{ok,data,request_id}` envelope；缺失/错误 key 均在采样前返回 401，机器码沿用 `UNAUTHORIZED`/`FORBIDDEN`。HTTP 200 的 `sample_status` 为 `complete/busy/timeout/failed/not_checked`，不代表可录。

`instance` 给出真实版本、模式、PID、实际监听端口、本实例 ready 状态与冻结数据目录；`desktop.startup/current` 分开静态启动回执和动态桌面证据；`tools` 给出四个工具的路径、来源、状态、原因码；`hotkeys.stop/chapter` 给出支持/注册/组合键/策略/原因；`backends` 给出窗口/显示器/区域策略和音频支持。事实附采样时间与证据类型，路径仅是 `path_only`，音频设备及实际捕获均 `not_checked`，章节空闲未注册是 `idle`。Headless 无 UI 要求不代表桌面实际可用。

总截止 3 秒覆盖 owner 投递与探针，UI/API 共享单一额度；超时后未结束的原生调用或未消耗的 owner 回调继续占额度，重试可能返回 `busy`。无 refresh/watch/自定义 timeout：合法未知 query 与 capabilities 一样被忽略，重复/空 key 或错误编码沿用 400。字段为固定 snake_case 和英文机器码，不随 UI 语言变化。健康读取不启动捕获/工具进程、不枚举设备、不自动修复，也不改变授权。任务状态、停止和结果仍使用 recordings/history/plans API；health 不是录制前必经步骤，不据此自动重试失败录制。详细字段见 [API 文档](docs/api.md#on-demand-read-only-health)。

## 性能追踪（本地诊断）

录制意图可附带一个客户端发送时间戳，用于本地延迟诊断：

```http
X-Agent-Sent-At: 2026-07-15T00:00:00.000Z
```

该值完全由客户端提供，作为**不可信提示**处理。服务器仅做基本合理性校验（-60 秒到 +5 分钟），并写入独立的 `client_hints` 字段。它**不会**影响请求成败，也**不会**进入服务器端延迟分位数或 SLO。

录制创建成功时，响应会新增可选字段 `performance_trace_id`：

```json
{
  "status": "requires_user_confirmation",
  "confirmation_id": "conf_xxx",
  "recording_id": "rec_xxx",
  "performance_trace_id": "trace_xxx"
}
```

该标识会同时写入本地性能追踪文件 `<data-dir>\perf\recording-traces.jsonl`，记录 `intent.accepted`、`confirmation.created`、`confirmation.shown`、`capture.start_requested`、`capture.backend_start_returned`、`capture.first_frame_observed` 等阶段事件。性能追踪只是本地诊断数据，与审计日志分离；**不包含** API key、完整输出路径、窗口标题、FFmpeg 完整参数、progress 原始文本或原始 `X-Agent-Sent-At` 头内容。

`capture.backend_start_returned` 仅表示后端 `Start()` 调用已返回，**不是**首帧已编码或已写入的证据。

`capture.first_frame_observed` 只在后端产生可信证据后发出。默认 FFmpeg 视频链路（`display`/`window`/`region`）要求 `-progress pipe:1` 报告 `frame >= 1`、`total_size > 0` 且进度组正常结束；它表示首个可观测编码/复用进度的保守上界。实验性 WGC continuous 链路则使用经过协议认证的 `FIRST_FRAME` 事件，要求源帧已被时间线接受并成功完成 GPU 到 BGRA 的复制。两者都不是物理磁盘刷写完成或最终 MP4 已通过校验的证据。

当前追踪覆盖“请求受理 → 本地确认 → 后端启动返回 → 首帧进度证据”路径。模型思考耗时不在服务端测量；cold/warm 分组的 P50/P95 聚合已通过 `/capabilities.perf_summary` 暴露（详见下文“检查能力”一节）。

批准后（使用麦克风时）额外发出的生命周期事件：

| 事件 | 触发时机 | 说明 |
| --- | --- | --- |
| `microphone_prepare_started` | capture-safe barrier 已完成，音频 worker 正在打开麦克风。 | 显示 `preparing` UI；尚未捕获屏幕。 |
| `microphone_ready` | 音频 worker 已产生可信音频样本。 | 触发 3-2-1 倒计时。 |
| `countdown_started` | 3-2-1 倒计时 UI 开始。 | 尚未开始屏幕捕获；`elapsed_seconds` 仍为 `0`。 |
| `video_first_frame` | 独立视频 worker 报告首个可信帧。 | 进入 `recording` 状态并显示红色 REC；`StartedAtUtc` 在此时设置。 |
| `capture_ended` | 屏幕捕获停止（达到计划时长或用户主动停止）。 | 立即隐藏 REC UI；进入 `finalizing`。 |
| `finalization_completed` | 音频裁剪、合流、ffprobe 校验和 bundle 生成完成。 | 之后发布终态（`completed`/`failed`）。 |

`ensure-running` 的冷/热握手现在可以通过一次性上下文关联到录制 trace。CLI 成功完成 `ensure-running` 后，会在 `<data-dir>\runtime\ensure-contexts` 下原子创建一个短期上下文文件，并在 JSON 输出中返回：

- `startup_kind`: `cold`（本次新启动服务）或 `warm`（复用已有服务）
- `ensure_elapsed_ms`: 本次 `ensure-running` 握手的总墙钟耗时（毫秒）
- `ensure_context_id`: 一次性上下文 ID，例如 `ensure_<32 位十六进制>`；仅在 `ensure_context_available=true` 时出现
- `ensure_context_header`: 固定为 `X-Agent-Recorder-Ensure-Context`；仅在 `ensure_context_available=true` 时出现
- `ensure_context_available`: `true` 表示上下文文件已创建，`false` 表示创建失败但 ensure 仍成功

AI agent 应在紧接着的下一次录制创建请求中透传该 header：

```http
X-Agent-Recorder-Ensure-Context: ensure_<32 位十六进制>
```

`POST /api/v1/recordings` 与 `POST /api/v1/recordings/quick` 均支持该可选 header。服务端只使用 header 中的 ID 从本地上下文目录读取并一次性消费；header 绝不会被解释为文件路径。可信的 `cold`/`warm` 标签、本次握手耗时 `ensure_elapsed_ms` 以及服务启动耗时 `service_startup_elapsed_ms` 均来自服务端消费的本地上下文，不是任意客户端 header 自报。

`startup_elapsed_ms` 与 `ensure_elapsed_ms` 的区别：

- `startup_elapsed_ms` 是服务进程从启动到 ready 的耗时；`warm` 时它是当前复用服务当初启动的耗时，不是本次握手耗时。
- `ensure_elapsed_ms` 是本次 `ensure-running` 从入口到服务身份验证成功并准备返回结果的完整墙钟耗时，同时覆盖 `cold` 与 `warm`。

如果上下文缺失、过期、格式非法、服务实例身份（PID + `ready_at`）不匹配、删除/claim 失败或已被消费，服务端不会写入可信 cold/warm 字段，也不会影响 API 状态码、confirmation、Consent Invariant 或录制结果；录制 intent 仍会正常进入原有确认路径。消费失败时，trace 中可能仅出现 `ensure_context_status`（值为 `missing`、`invalid`、`expired`、`instance_mismatch`、`reused` 或 `unavailable` 之一），且不含敏感路径或异常全文。同一 ID 并发或重复消费时，只有一个 trace 能获得 `consumed` 与可信 startup 字段，其余 trace 的 `ensure_context_status` 会表现为 `reused` 或 `missing`。

上下文文件写入采用同目录随机临时文件 + 原子落位，异常路径会清理临时文件；文件与进程内消费 tombstone 的默认 TTL 均为 5 分钟，并受数量上限约束，不会随历史消费次数无限增长。`ensure-running` 失败结果不会输出 `startup_kind`、`ensure_elapsed_ms`、`ensure_context_id`、`ensure_context_header`、`ensure_context_available` 等字段。

可信上下文字段会以顶层可选字段形式出现在该 trace 的后续事件中：

- `startup_kind`: `cold|warm`
- `ensure_elapsed_ms`: 本次 ensure-running 握手耗时（毫秒）
- `service_startup_elapsed_ms`: 服务启动耗时（毫秒；warm 时为当初启动耗时）
- `ensure_context_status`: `consumed` 或失败原因枚举

这些字段不会出现在 `client_hints` 中；原始 `ensure_context_id`、上下文文件路径、ready 文件内容和 header 原文均不会进入 performance JSONL 或审计日志。

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
  "startup_elapsed_ms": 850,
  "startup_kind": "warm",
  "ensure_elapsed_ms": 120,
  "ensure_context_id": "ensure_0123456789abcdef0123456789abcdef",
  "ensure_context_header": "X-Agent-Recorder-Ensure-Context",
  "ensure_context_available": true
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

用途：确认服务已启动，并读取是否支持 `display`、`window`、`region`、嵌套录制、Chapter Marks 和确认机制。

该接口不需要 API key。

**WGC 能力边界**：display/region 的实验性 WGC 路径仍是内部受控功能，默认关闭；固定 window 有独立的公共严格请求，见“窗口捕获语义”。该请求只在 `recording.window_surface.supported=true` 时可用；WGC 探测失败不会回退桌面矩形。严格窗口表面请求时长为 1–1800 秒，并使用正常本地逐次确认；这是配置和执行上限，不代表已经完成该设备/窗口/端点的 30 分钟 A/V 稳定性验收，也不表示完整直播录制已经就绪。system loopback 变体还要求 `recording.window_surface.system_audio_supported=true` 和一个可用输出端点。display、region 与未声明严格语义的旧 WGC 路径仍为最多 60 秒。

**音频能力**：麦克风和系统声音均由隔离的 Windows WASAPI helper 捕获，最终合流编码为 AAC；FFmpeg dshow 仅作为显式诊断回退。蓝牙 Hands-Free 输入会被动识别传输类型，并自动发现同一设备容器的渲染端点，通过静音 render prime 建立并保持 HFP 双工链路。AirPods Pro 与 Focal Bathys 已通过真实产品路径验收，但不同设备、固件和驱动仍可能失败；失败会进入明确终态，不会发布静音成功视频。终态响应和审计包含 capture strategy、配对证据、render-prime 延迟、current/max gap、恢复和 discontinuity 诊断。`recording.audio` 保留为兼容性数组，现在报告 `["microphone", "system_audio"]`。`recording.audio_capabilities.microphone` 与 `.system_audio` 都返回 `supported: true`，状态为新鲜的 `ready`、`no_devices` 或 `unavailable`。系统声音请求仍须本地确认；批准的 render endpoint 在本次录制中保持固定，默认输出切换不会自动跟随，切回批准端点后执行有界同端点恢复并通过连续性指标披露缺口。麦克风和系统声音不能在同一请求中同时启用。

`recording.window_surface` 报告 WGC 窗口运行时当前探测结果、失败原因、1–1800 秒范围和逐次本地确认要求。`long_run_readiness="bounded_duration_only"` 与 `long_run_stress_tested=false` 表明能力探测不等于长时 A/V 验收。只有 WGC、render endpoint 与 WASAPI helper 协议探测都可用时，`system_audio_supported` 才为 true；`system_audio_helper_status`/`system_audio_helper_reason_code` 单独说明 helper 状态；若没有可用 endpoint，helper 状态为 `not_checked`。system audio 是批准的 Windows 输出端点 loopback，捕获该端点播放的系统声音，**不是所选窗口独占的音轨**。

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

返回中还包含 `interaction.stop_controls` 字段，说明本地停止控制支持情况：

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

- `floating_button`：是否为每条活动录制显示悬浮停止按钮。
- `tray_stop`：托盘菜单是否提供停止入口。
- `global_hotkey.supported`：是否支持全局停止热键。
- `global_hotkey.registered`：热键是否实际注册成功（tray 模式通常为 true，冲突时可能为 false）。
- `global_hotkey.gesture`：热键组合，如 `Ctrl+Shift+F10`。

Chapter Marks 的能力位于返回顶层 `chapter_marks`：

```json
{
  "chapter_marks": {
    "supported": true,
    "endpoint": "/api/v1/recordings/{recording_id}/marks",
    "local_hotkey": {
      "supported": true,
      "registered": false,
      "gesture": "Ctrl+Shift+F11",
      "registration_policy": "while_recording"
    }
  }
}
```

- `chapter_marks.supported` 表示 marks API/domain/bundle 能力，独立于本地 UI 是否存在。
- tray 宿主的 `local_hotkey.supported=true`；`registered` 只在至少一条录制处于 exact `recording` 状态时可能为 true，空闲、准备、倒计时、停止或保存状态均为 false。
- 本地按一次 `Ctrl+Shift+F11` 会对 UI 线程快照中所有 exact `recording` 录制各添加一个标记；outer/inner 并发录制各使用自己的首帧单调时间轴。准备中或 finalizing 的条目会被忽略。
- headless 宿主仍报告 `chapter_marks.supported=true`，但 `local_hotkey.supported=false`、`registered=false`、`gesture=null`，不得据此声称存在本地热键。

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

### 性能摘要

`/capabilities` 返回 `perf_summary` 字段，提供有界、只读的历史性能统计摘要。该数据属于诊断数据，不是审计日志，**不包含** trace ID、recording ID、confirmation ID、输出路径、API key、context ID 或 header 原文。

只有消费了可信 `ensure-running` 上下文、且 `startup_kind` 为 `cold`/`warm`、`ensure_context_status=consumed`、`ensure_elapsed_ms` 非负的 trace 才会被分组统计；其余 trace 计入 `unclassified_trace_count`。

```json
{
  "perf_summary": {
    "schema_version": 1,
    "status": "available",
    "generated_at": "2026-07-18T00:00:00.000Z",
    "window": {
      "max_traces_per_group": 50,
      "source": "local_rolling_jsonl"
    },
    "quality": {
      "malformed_line_count": 0,
      "unsupported_schema_count": 0,
      "discarded_sample_count": 0,
      "unclassified_trace_count": 3,
      "reason_code": null
    },
    "groups": {
      "cold": {
        "trace_count": 4,
        "quality": "preliminary",
        "metrics": {
          "ensure_running_ms": { "sample_count": 4, "p50": 730.0, "p95": 820.0 },
          "service_startup_ms": { "sample_count": 4, "p50": 165.0, "p95": 180.0 },
          "request_to_confirmation_shown_ms": { "sample_count": 4, "p50": 120.0, "p95": 150.0 },
          "confirmation_shown_to_approved_ms": { "sample_count": 3, "p50": 250.0, "p95": 310.0 },
          "approved_to_first_frame_progress_ms": { "sample_count": 4, "p50": 740.0, "p95": 810.0 },
          "request_to_first_frame_progress_ms": { "sample_count": 4, "p50": 1120.0, "p95": 1280.0 }
        }
      },
      "warm": {
        "trace_count": 12,
        "quality": "representative",
        "metrics": {}
      }
    }
  }
}
```

状态值：

- `available`：存在至少一条有效的 `cold` 或 `warm` trace，且本次刷新未发生读取边界、解析故障或数据丢弃。
- `no_data`：perf 文件缺失/为空，或没有合格 trace，且本次刷新未发生故障。完整结构和零计数仍然返回。
- `degraded`：读取/解析出现局部故障，或部分有效样本被丢弃，但仍返回已累积的部分统计。`reason_code` 为稳定枚举，不暴露异常文本、文件路径或任何 ID。

状态优先级（从低到高）：`no_data` < `available` < `degraded`。只要本次刷新出现数据损失（读取边界、malformed line、unsupported schema、被丢弃的 context/event 样本），即使仍有有效 trace，也返回 `degraded`。

读取边界与隐私：provider 以只读方式扫描 `<data-dir>\perf\recording-traces.jsonl` 及其滚动历史 `.1.jsonl`、`.2.jsonl`、`.3.jsonl`，并实施以下多维边界以防止单点大文件/畸形数据影响服务：

| 边界 | 默认值 | 说明 |
| --- | --- | --- |
| 文件数量 | 4 | 当前 base 文件 + 最多 3 份历史滚动文件 |
| 单文件字节 | 5 MiB | 按 UTF-8 字节计数，含换行符 |
| 总字节 | 20 MiB | 跨文件累计 UTF-8 字节 |
| distinct trace 数 | 10 000 | 按唯一 `trace_id` 计数，不是 event line 数 |
| event line 数 | 100 000 | 跨文件累计原始行数 |
| 单行长度 | 1 MiB | 按 UTF-8 字节限制单行，限制包含行正文及其终止符（LF、CRLF 或 CR）；末尾无换行的最后一行仅计算正文。文件起始的 UTF-8 BOM 不计入单行长度，但会计入单文件与总字节限制。超长行触发 `read_boundary_reached` 并停止扫描。无效 UTF-8 序列被安全处理。 |
| 每组 trace | 50 | 每组 cold/warm 只保留最近 50 条 trace |

到达任一读取边界后，扫描停止，但已经处理的有效 trace 和指标仍然保留并返回，`status` 为 `degraded`，`reason_code` 通常为 `read_boundary_reached`。边界值只通过 `window.max_traces_per_group` 等公开字段暴露，不返回绝对路径。

重复事件选择：同一 trace 的同一事件在多个滚动文件中重复出现时，provider 选择 `elapsed_from_intent_ms` 最早的合法值，不依赖文件枚举顺序。若先读到无效值（NaN/Infinity/负数/超 2 小时），后读到合法值，则用合法值替换。`elapsed` 相同时以更早的 `timestamp_utc` 作为 tie-breaker。

Context 指标校验与冲突检测：`ensure_elapsed_ms` 和 `service_startup_elapsed_ms` 均要求非负、有限且不超过 2 小时（7 200 000 ms）数据损坏防护上界。非法 `ensure_elapsed_ms` 会导致该 trace 无法进入 cold/warm 分组；非法 `service_startup_ms` 仅跳过该指标样本但保留 trace 分组。两者均计入 `discarded_sample_count`。

此外，provider 对 `startup_kind`、`ensure_context_status`、`ensure_elapsed_ms`、`service_startup_elapsed_ms` 执行顺序无关的一致性检查：同一非空值重复出现合法；同一 trace 出现两个不同非空值即判定为 context conflict。冲突 trace 不计入 cold/warm 分组，计入 `unclassified_trace_count` 并增加 `discarded_sample_count`；若仍有其他有效 trace，summary 为 `degraded`/`partial_data`。字段在部分事件缺失、在另一些事件存在单一值不算冲突，只有多个不同实际值才算冲突。

缓存与故障策略：结果缓存 10 秒，并发刷新单线程执行。正常的边界 partial 结果会按原样返回并可能更新缓存，**不会**触发 stale-cache 回退。只有真正的文件打开/读取失败（例如打开文件时发生 `IOException`）才会让 provider 返回最近一次缓存快照的深拷贝，标记为 `status=degraded`、`reason_code=stale_snapshot`；stale 响应的 `generated_at` 为本次请求时间，统计值来自缓存快照。无缓存时发生读取失败，返回全零的 `degraded` 摘要，`reason_code=read_error`（内部故障时为 `unexpected_provider_error`）。`ApiServer` 在 `GET /capabilities` 中还会再捕获 provider 异常，返回 `reason_code=provider_error` 的 `degraded` 摘要，确保 `/capabilities` 始终 200。

常见 `reason_code`：

| reason_code | 含义 |
| --- | --- |
| `read_boundary_reached` | 读取到单文件/总字节/trace/event line/单行长度边界 |
| `read_error` | 文件打开/读取失败（如路径指向目录） |
| `partial_data` | 存在 malformed line 或 unsupported schema，但无明确边界 |
| `stale_snapshot` | 刷新失败，返回缓存快照 |
| `unexpected_provider_error` | provider 内部未预期错误且无缓存 |
| `provider_error` | ApiServer 捕获到 provider 抛异常（仅出现在 HTTP 响应） |

分组 `quality` 是数据质量标签，不是性能达标结论：

- `preliminary`：该组少于 20 条 trace。
- `representative`：该组达到或超过 20 条 trace。

指标仅在存在有效配对样本时出现，每个指标有独立的 `sample_count`。百分位采用 nearest-rank 算法（`rank = ceil(P/100 * N)`，1-indexed），毫秒值保留一位小数。

指标语义：

- `ensure_running_ms`：可信的 `ensure_elapsed_ms`。
- `service_startup_ms`：可信的 `service_startup_elapsed_ms`；`warm` 组表示被复用服务**最初**的启动耗时，不是本次 warm ensure 的额外耗时。
- `request_to_confirmation_shown_ms`：`confirmation.shown - intent.accepted`。
- `confirmation_shown_to_approved_ms`：`confirmation.approved - confirmation.shown`，仅统计实际批准链路。
- `approved_to_first_frame_progress_ms`：`capture.first_frame_observed - confirmation.approved`。字段名中的 `progress` 是刻意的：它只表示 FFmpeg 报告首个可观测编码/复用进度的保守上界，不是物理采集首帧、不是磁盘刷写完成、也不是 MP4 可播放的证据。
- `request_to_first_frame_progress_ms`：`capture.first_frame_observed - intent.accepted`，反映服务端受理请求后的完整本地链路，不含 agent 思考时间和 `ensure-running` 之前的时间。

`client_hints.agent_to_server_hint_ms` 等客户端提供的时长**永远不会**进入服务端百分位。

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
- 对“录制显示器 N”优先使用 `windows_display` quick target；`quick_recipes` 仅在当前快照有可靠编号时生成对应的实际正整数模板
- 如果 `context.windows.active == null`，应让用户聚焦窗口或改用 `selected_region`
- 如果 `context.last_selected_region == null`，不要假设存在上次选区；可改用 `selected_region` 先让用户选区
- 显示器/窗口枚举失败时，`/capabilities` 仍返回 200，错误信息在对应 `error` 字段中

### 就绪文件（推荐）

服务启动成功后还会写入 `<data-dir>\runtime\ready.json`，AI Agent 可以轮询该文件判断服务就绪，无需盲轮询 `/capabilities`。

## 1.1 检查权限

```http
GET /permissions
```

不需要 API key。`screen_capture` 与 `output_directory` 为本地授予。麦克风和系统声音均已支持，其状态诚实反映可用性：`available`、`no_devices` 或 `unavailable`。本版本不探测真实 Windows 麦克风或 render endpoint ACL，因此不伪造 `granted`。

```json
{
  "screen_capture": { "status": "granted" },
  "microphone": { "supported": true, "status": "available" },
  "system_audio": { "supported": true, "status": "available" },
  "output_directory": {
    "status": "granted",
    "default_path": "C:\\...\\.local-data\\Videos",
    "selection_ui": true
  }
}
```

## 1.2 列出音频设备

```http
GET /audio/devices
```

不需要 API key。通过 FFmpeg dshow 枚举真实麦克风输入设备，并通过 CoreAudio 枚举 active render endpoint。兼容字段 `status` 保持为麦克风状态，同时提供 `microphone_status` 和 `system_audio_status`；两侧状态均为 `ready`、`no_devices` 或 `unavailable`。`microphone_supported` 与 `system_audio_supported` 均为 `true`。两侧枚举相互隔离，某侧失败时仍返回 HTTP 200 且该侧数组为空。

请求麦克风录制时，Agent Recorder 会启动独立的 Windows WASAPI helper（`AgentRecorder.AudioHelper.exe`），按 CoreAudio endpoint 精确采集。本接口返回的 `id` 仍为 FFmpeg dshow alternative name，以保持 API 兼容；Agent Recorder 内部会将其映射为对应的 CoreAudio endpoint ID。蓝牙 Hands-Free endpoint 会被动分类，并可自动配对同一 ContainerId 的 render endpoint 进行 duplex prime。设备可枚举且状态为 active 仍不代表所有驱动都能初始化；配对、初始化失败或运行期样本中断会作为录制失败显式返回。如需显式回退到 FFmpeg dshow 诊断后端，可在启动 Agent Recorder 前设置环境变量 `AGENT_RECORDER_AUDIO_BACKEND=dshow`。

设备枚举解析器同时支持 portable 包内 FFmpeg 的经典 `[dshow]` / `[dshow @ ...] DirectShow audio devices` 分段格式和 FFmpeg 8.x 的 `[in#N @ ...] "设备名" (audio)` tagged 格式。仅接受两类可信 logger 前缀：经典行必须以 `[dshow]` 或 `[dshow @ identity]` 开头，tagged 行必须以 `[in#N @ identity]` 开头（`N` 为至少一位数字，`identity` 非空）。引号内的 friendly name 与 alternative name 采用 consumed-length 解析，引号前后出现额外文本（如 `prefix "Name" (audio)` 或 `"Name" (audio) suffix`）均会被拒绝；同时解码 FFmpeg 的 `\"` 与 `\\` 转义并保留普通反斜杠（如 `\wave_{GUID}`）。任何不完整或畸形的设备记录——例如无效 logger 前缀、缺少 alternative name、孤立的 alternative、被其他行打断的候选设备、quoted value 后带尾部垃圾，或设备与无设备标记同时出现——都会使整个 listing 被视为无法识别，返回 `status: "unavailable"`。缺少可信 logger 前缀的行（包括普通 `warning:` 或其他 logger 输出）会被安全忽略，绝不会生成设备。classic 无设备标记必须位于已由可信 `DirectShow audio devices` header 开启的 audio section 内；tagged 无设备标记可直接来自可信 input logger。完整的 tagged 视频记录（`(video)` friendly name 加匹配的 alternative）可在任意顺序被安全忽略，不会中断音频枚举。解析器绝不返回部分音频设备列表。只有完整且可识别的 listing（有设备或无设备）才接受不同版本正常的 listing 退出码差异（`1`、`0`、`-2`），并返回 `ready` 或 `no_devices`。

`id` 是 FFmpeg dshow 返回的 alternative name（示例展示的是不带 `audio=` 前缀的 alternative-name ID），调用者在后续请求中必须**原样传递**，不得自行添加 `audio=` 前缀；Agent Recorder 会将其映射为 WASAPI helper 所需的 CoreAudio endpoint ID。仅当显式启用 dshow 回退时，Agent Recorder 才会在构造 FFmpeg 参数时负责添加 `audio=` 前缀。

```json
{
  "status": "ready",
  "microphone_supported": true,
  "input_devices": [
    {
      "id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}",
      "name": "Microphone (Realtek(R) Audio)",
      "is_default": true,
      "state": "active",
      "is_muted": false,
      "volume_percent": 75
    }
  ],
  "microphone_status": "ready",
  "system_audio_status": "ready",
  "system_audio_supported": true,
  "output_devices": [
    {
      "id": "{0.0.0.00000000}.{RENDER-ENDPOINT}",
      "name": "Speakers",
      "is_default": true,
      "state": "active",
      "direction": "render"
    }
  ]
}
```

`output_devices` 只包含 active render endpoint。其 `id` 是不透明的 CoreAudio endpoint ID，
后续请求必须原样作为 `audio.system_audio.device_id` 传回；省略该字段时使用当前 Windows
多媒体默认输出。列表把精确默认端点放在首位，之后按名称和 ID 确定性排序。

字段说明：

| 字段 | 类型 | 说明 |
|------|------|------|
| `id` | string | FFmpeg dshow alternative name，需原样回传。 |
| `name` | string | 人类可读设备名。 |
| `is_default` | boolean \| null | 当前 CoreAudio 多媒体默认捕获端点为 `true`；CoreAudio 不可查时回退到 dshow 提供的默认值；`null` 表示未知。 |
| `state` | string \| null | CoreAudio 端点 active 时为 `"active"`，否则为 `"inactive"`；`null` 表示未知。 |
| `is_muted` | boolean \| null | 端点被软件静音时为 `true`；`null` 表示未知。 |
| `volume_percent` | integer \| null | 主音量标量，四舍五入到 `0..100`；`null` 表示未知。 |

`is_default`、`state`、`is_muted`、`volume_percent` 每次请求都重新读取，**不**进入 10 秒的 dshow 枚举缓存。用户取消静音、切换默认设备后会立即反映到下一次调用。

`state` 为 `null` 或 `"active"` 不会阻断录制；`state` 为 `"inactive"` 会在弹出选区/确认 UI 之前直接失败。

当 `is_muted` 为 `false` 且 `volume_percent` 低于 `10` 时，确认 UI 会显示低音量警告。该警告不阻断录制，Agent Recorder 也不会自动修改系统音量。

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
      "name": "Display 2",
      "windows_display_number": 2,
      "bounds": { "x": 0, "y": 0, "width": 1920, "height": 1080 },
      "is_primary": true
    }
  ]
}
```

`id` 是兼容性的 API 选择 token，`display_id` 请求字段也使用它；其数字后缀
不承诺等于 Windows“设置 > 系统 > 显示 > 标识”中的屏幕序号。每个条目还提供
`windows_display_number`：来源是 GDI `MONITORINFOEX.szDevice`，只接受严格的
`\\.\DISPLAY<N>`（正的 ASCII 十进制后缀）形式，且活动快照内没有重复时才返回正整数；
否则为 `null`。该来源已在当前机器与 Windows“标识”显示的屏幕编号视图核对；这只是
当前显示配置下的观测，不是跨机器或跨驱动永不变化的稳定身份。能可靠获取时，
`name` 使用同一个 Windows 序号，因此 `id: "display_1"`、
`windows_display_number: 2`、`name: "Display 2"` 是合法且预期的组合。

对 quick API 的“录制显示器 N”请求，应直接使用 `windows_display` 和 Windows
标识数字。只有在使用原始 API 时，才从 `/displays` 或
`/capabilities.context.displays.items` 找到 `windows_display_number == N` 的条目，
再将该条目的 `id` 原样传给 `source.display_id`，不要自行拼接 `display_N`。若该字段为
`null`，应结合 `is_primary`、bounds、name 请求用户消歧，而不是猜测。原始 GDI
设备名、DisplayConfig 路径、adapter 信息和稳定身份指纹不会出现在公共响应中。

## 3. 列出窗口

```http
GET /windows?include_minimized=false&include_system_windows=false
X-Agent-Recorder-Key: <api-key>
```

用途：当用户明确要录制某个窗口时，AI agent 可以列出窗口并选择匹配项。响应中的 `capture_eligibility_reason_code` 为空表示该窗口符合可选的内容表面结构规则；非空时会说明排除原因（例如 owner 辅助窗、tool window、非激活瞬态窗、无标题、零面积或不与活动显示器相交）。该字段不包含窗口像素或额外窗口文本；测试注入枚举器可能返回 `not_evaluated`。常见“录当前窗口/选区录屏”请求优先使用 quick API。

未来窗口授权会对所有顶层 HWND 应用同一结构规则，再额外验证精确可执行文件身份、批准后的进程创建时间、当前用户/会话以及候选集合唯一性。`/windows` 是窗口清单，仍可能返回不符合项；只选择 eligibility reason 为空的窗口。若同一已批准程序有多个合格内容窗，后端会继续 fail closed 并报告歧义。

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
    "microphone": {
      "enabled": true,
      "device_id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}"
    }
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
| `target.type` | 是 | `primary_display` / `windows_display` / `active_window` / `selected_region` / `last_region` |
| `target.windows_display_number` | `windows_display` 必填 | Windows“设置 > 系统 > 显示 > 标识”显示的正整数；必须是 JSON integer，不能使用 API ID 后缀 |
| `target.selection_timeout_seconds` | 否 | 仅 `selected_region` 生效，默认 `120`，范围 `10..600` |
| `duration_seconds` | 否 | 转换为 `stop_condition: { type: "duration", seconds: n }`；不填则手动停止 |
| `video` | 否 | 透传到原始录制配置，默认值同原始 API |
| `audio` | 否 | 透传到原始录制配置；`audio.microphone.enabled` 或 `audio.system_audio.enabled` 可单独为 `true`。省略麦克风 `device_id` 时自动选择唯一 active/默认输入；显式 ID 来自 `input_devices`。省略系统声音 `device_id` 时使用当前 Windows 多媒体默认输出；显式 ID 来自 `output_devices`。批准的端点固定在本次录制中，麦克风与系统声音不能同时启用。若选中麦克风被静音，请求会在选区/确认 UI 前失败（`409 AUDIO_DEVICE_MUTED`）；若设备已知为 inactive，返回 `503 AUDIO_DEVICE_NOT_AVAILABLE`。应用不会自动取消系统静音；状态未知时不阻断 |
| `output` | 否 | 透传到原始录制配置 |
| `nested` | 否 | 透传到原始录制配置，使用现有 nested 规则 |

### Windows 显示器目标

**`windows_display`**：按当前显示快照中唯一可靠的 Windows 显示器序号录制。推荐直接使用 quick API：

```json
{
  "target": { "type": "windows_display", "windows_display_number": 2 },
  "duration_seconds": 30
}
```

这里的 `2` 是 Windows“标识”界面看到的数字，不是 `display_2`，也不能从
API ID 后缀、列表顺序、名称或 bounds 推测。服务端只接受正 JSON integer，
从同一次当前显示器快照匹配恰好一项，并把匹配项的 opaque `id` 交给现有录制流程。
找不到可靠唯一编号时返回 `SOURCE_NOT_FOUND`，应刷新 `/capabilities` 或 `/displays`
后重试，或请用户消歧；不会创建 recording 或确认窗。成功响应的
`quick.resolved_source` 会返回 `display_id`、`windows_display_number` 和对齐后的
`name`。成功创建后仍须本地用户确认，HTTP API 不能自批准。

### 其他目标类型

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
    "recording_id": "rec_xxx",
    "performance_trace_id": "trace_xxx",
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

### 可选的有界创建等待

两个创建接口都支持 opt-in 的生命周期等待参数：

```http
POST /recordings?wait_for=recording&wait_ms=25000
POST /recordings/quick?wait_for=recording&wait_ms=25000
```

`wait_for` 当前只接受 `recording`。省略 `wait_for` 时沿用原有的立即创建响应；此时
若单独提供 `wait_ms`，请求无效。只有提供 `wait_for=recording` 时，`wait_ms` 才可选，
省略即使用 `25000`；显式值必须是 `1..25000` 的 JSON query integer。未知
`wait_for`、非法/越界 `wait_ms` 都在来源解析、设备枚举、选区 UI、确认和捕获副作用之前
返回 `400 INVALID_ARGUMENT`。

等待预算从 recording 创建成功后才开始。服务等待两者中最先发生者：已有可信首帧门槛
之后的公共 `recording` 状态，或 `rejected`、`expired`、`cancelled`、`failed`、
`completed` 终态。批准、preparing、countdown、backend Start 和 plan revalidation
都不是成功里程碑；该参数是只读观察，不会批准、拒绝、启动、停止或缩短录制。quick 的
`selected_region` 会先完成选区再创建 recording；取消/超时/不可用时沿用原有业务响应，
不会伪造 `wait` 或 `recording` 对象。

创建成功且 recording 仍存在时，原有的 `recording_id`、`confirmation_id`、`summary`、
`bundle`、`performance_trace_id` 以及 quick 元数据全部保留，并新增当前公共 `status`、
与 `GET /recordings/{recording_id}` 完全相同的规范 `recording` 投影，以及：

```json
{
  "status": "pending_confirmation",
  "recording_id": "rec_xxx",
  "confirmation_id": "conf_xxx",
  "summary": {},
  "performance_trace_id": "trace_xxx",
  "recording": { "recording_id": "rec_xxx", "status": "pending_confirmation" },
  "wait": {
    "wait_for": "recording",
    "requested_ms": 25000,
    "elapsed_ms": 25000,
    "timed_out": true,
    "goal_reached": false,
    "terminal": false
  }
}
```

超时仍返回 HTTP 200 和真实当前状态，不改变录制。`goal_reached` 只有可信视频首帧
转换到 `recording` 时为 `true`；`terminal` 只有终态为 `true`。如果观察者启动前已经
进入终态，返回 `terminal: true`，不会伪报 `goal_reached`。`performance_trace` 使用稳定
事件 kind `creation_recording`，只记录有界等待测量和生命周期 ID，不记录请求体或输出
路径。能力发现通过 `/capabilities.interaction.creation_wait` 暴露 endpoints、支持值、
默认/最大等待和 `trusted_first_frame_or_terminal` 里程碑。

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

## 5.1 一次性有限无人值守计划

该能力默认关闭，只适用于一次固定区域、无音频、自然唤醒且需要交互桌面的录制。AI agent 只能提交 setup 并查询状态，不能通过 HTTP 选择区域、批准 Lease 或扩大授权范围。使用前先读取：

```text
GET /capabilities -> unattended_lease
```

只有 `supported=true`、`current_enabled=true`、`execution_supported=true` 时才应发起请求；否则向用户说明需要在本地托盘安全控制中启用或当前宿主不支持执行。

创建或恢复幂等 setup：

```http
POST /plans
X-Agent-Recorder-Key: <api-key>
Idempotency-Key: <本次规范请求的稳定键>
Content-Type: application/json
```

```json
{
  "recording_spec": {
    "source": { "type": "fixed_region" },
    "audio": { "mode": "none" },
    "duration_seconds": 30,
    "countdown_seconds": 0,
    "backend": "ffmpeg-region",
    "output": {
      "directory": "D:\\Videos",
      "filename": "scheduled-recording.mp4"
    }
  },
  "schedule": {
    "kind": "once",
    "start_at": "2026-09-17T10:00:00Z",
    "latest_start_at": "2026-09-17T10:02:00Z",
    "planned_end_at": "2026-09-17T10:02:30Z"
  },
  "requested_authorization": {
    "mode": "standing_lease",
    "expires_at": "2026-09-17T10:03:00Z",
    "max_runs": 1,
    "max_duration_seconds": 30
  }
}
```

约束：`duration_seconds` 为 `1..600`；Lease 最长 1 小时；最晚启动宽限期最长 5 分钟；完整录制必须能在 `planned_end_at` 前结束；`expires_at` 必须晚于计划结束；输出目录必须是绝对路径，文件名不得含路径分隔符。

成功返回 HTTP `202`，包含 `setup_intent_id`、`status_url`、`status_version_cursor`、`requires_local_action` 和 `next_action`。同一个 `Idempotency-Key` 配合同一规范请求返回原 setup；绑定不同请求时返回 `409 IDEMPOTENCY_KEY_REUSED`。

查询或长轮询：

```http
GET /plan-setups/{setup_intent_id}
GET /plan-setups/{setup_intent_id}?since_status_version=<opaque-cursor>&wait_ms=25000
```

cursor 必须原样回传。`wait_ms` 最大 25000。响应可能包含 `run_id`、`recording_status_url`、`started_at` 和 `completed_at`；出现 `recording_status_url` 后，再按普通录制状态接口查询最终媒体和输出路径。不要把 `scheduled` 当作已经开始，只有返回的录制状态达到可信 `recording` 才能向用户报告已开始。

`standing_lease` 模式下，本地 UI 会要求用户重新选区并批准有界 Lease。到点时应用再次验证显示器身份、物理区域、用户会话、输出文件、执行窗口、Lease、安全开关和一次性 proof。撤销、Stop All、锁屏/会话断开、睡眠、目标或输出变化、文件已存在及错过窗口都 fail closed，不会自动换目标、换路径、重试或回退成普通录制。

### 持久化计划执行状态

GET /api/v1/plan-setups/{setup_intent_id} 描述的是计划建立、本地选区和批准流程；它不是录制执行结果。计划 setup 完成后，Agent 应使用下面经过 API key 认证的只读端点查询实际持久化的执行状态，应用重启后也不依赖内存中的录制列表：

~~~~http
GET /api/v1/plans/{plan_id}/status
X-Agent-Recorder-Key: <api-key>
~~~~

响应使用标准 ok/data/request_id 包装。kind 为 once、daily 或 weekly；plan_status 只是计划持久化状态，不能单独证明录制成功。occurrence_count 是已物化实例数。latest_occurrence 按计划窗口开始时间倒序、再按 occurrence ID 倒序；next_occurrence 是最早的非终态实例（也包括执行中的实例）。每个实例摘要包含窗口、实例状态/原因、可选 Run 状态/原因和产物证据；首版不返回历史分页。

Occurrence 与 Run 来自同一个 SQLite 读取快照，且必须构成可达的生命周期组合：`run_created` 必须关联仍处于非终态的 Run；`completed` 必须关联无终态原因的 `settled` Run；带 Run 的 `blocked` 必须与 Run 具有相同的异常终态原因。无 Run 的终态只有在符合领域模型约束时才接受（例如 `missed` 不允许有 Run）。关联不一致时 fail closed，返回 `503 PLAN_STATUS_UNAVAILABLE`，不会把它呈现为成功结果。

~~~~json
{
  "ok": true,
  "data": {
    "plan_id": "recurring-plan-...",
    "kind": "daily",
    "plan_status": "enabled",
    "schedule_exhausted": true,
    "occurrence_count": 1,
    "next_occurrence": null,
    "latest_occurrence": {
      "occurrence_id": "recurring-occurrence-...",
      "window_start_utc": "2026-10-01T01:30:00Z",
      "window_end_utc": "2026-10-01T02:02:00Z",
      "status": "completed",
      "terminal_reason_code": null,
      "run_id": "run-...",
      "run": {
        "run_id": "run-...",
        "status": "settled",
        "terminal_reason_code": null
      },
      "output_path": "C:\\Users\\alice\\Videos\\recurring-20261001-013000.mp4",
      "output_path_recorded": true,
      "output_file_exists": true
    }
  },
  "request_id": "..."
}
~~~~

一次性计划的 schedule_exhausted 为 null。周期计划该字段来自持久化 schedule cursor；若为 true 且 next_occurrence 为 null，表示 cursor 已无后续时间槽且所有已物化实例均已终结，本计划不会再自动运行。

成功且已验证的结算会把媒体探测实际报告、并与该 occurrence 精确批准的冻结目标匹配的路径作为不可变证据保存。此时 `output_path` 返回该路径，`output_path_recorded` 为 true；`output_file_exists` 是查询时对该确切路径的文件存在性快照（true 或 false），没有路径时为 null。文件后来被删除只会让下一次查询的 `output_file_exists` 变成 false，不会清除已记录路径或改写 Run 状态。

schema v16 之前已结算的历史记录没有可信的实际路径历史，迁移不会从授权目录/冻结文件名推导路径，也不会扫描 Videos 或猜文件名；它们继续返回 `output_path: null`、`output_path_recorded: false`、`output_file_exists: null`。当前 settled 记录若缺少证据、证据损坏/跨 Run 或 Occurrence 关联、或路径越出批准目录，则 fail closed，返回 `503 PLAN_STATUS_UNAVAILABLE`，不会伪造路径。

状态查询限定为当前 Windows 用户和交互会话。未知或属于其他身份的计划均返回 404 PLAN_NOT_FOUND；格式非法的 ID 返回 400 INVALID_ARGUMENT。持久化状态不可用或关联损坏时 fail closed，返回 503 PLAN_STATUS_UNAVAILABLE。

### 计划执行历史分页

若要查看该拥有计划的全部持久 occurrence（包含尚未启动的实例），使用只读分页接口：

~~~~http
GET /api/v1/plans/{plan_id}/history?limit=20&cursor=<opaque>
X-Agent-Recorder-Key: <api-key>
~~~~

范围限于当前 status gateway 已拥有的固定区域 standing once、required once 和 recurring daily/weekly；kind 沿用 `once`、`daily`、`weekly`。`limit` 默认 20，范围 1–100；只接受 `limit`、`cursor`，未知/重复参数、无效 limit、空/过长/格式错误 cursor 均返回 400 `INVALID_ARGUMENT`。响应为标准 `ok/request_id/data` envelope；data 包含 `plan_id`、`kind`、`items`、`next_cursor`、`page_size`。条目按窗口开始时间倒序、occurrence ID 的 SQLite BINARY 顺序正序排列，包含 `created_at_utc`、窗口、原始 occurrence 状态/原因、可选 Run 原始状态/原因、`execution_status_code` 和已验证输出证据。`run_id: null` 表示没有登记 Run，不代表有视频；媒体缺失或不可读只影响查询时的 `output_file_exists`，不改写持久录制状态；`legacy_path_unavailable` 的路径保持 null。

每页均使用当前 Windows SID 与 `CaptureAuthorizationSessionBinding.Current` 重新验证拥有关系，cursor 不能替代授权边界。未知、外来 SID 或其他 session 的计划统一 404 `PLAN_NOT_FOUND`；存储不可用或拥有数据不一致为 503 `PLAN_HISTORY_UNAVAILABLE`。首个 cursor 绑定 Plan、身份上下文、查询版本及首查时的 SQLite rowid 插入水位（使用全表最大 rowid，随后仍按 Plan 过滤）。受支持的 occurrence 写入使用普通 rowid 自动分配、不会指定 rowid，且没有删除路径；因此后续插入即使窗口日期较早也不会进入该游标的后续页。分页使用 keyset 而非 offset。水位只固定成员集合；每页的状态和输出证据来自各自一致的 SQLite 读取快照，不承诺跨页冻结可变状态。每页候选最多 `limit+1` 行，当前返回行及生命周期/证据验证最多 `limit` 条，不受 status 投影 512 条上限影响。

`/capabilities` 仅在生产拥有计划查询 gateway 接通时公布 `plan_execution_history`、endpoint、页大小范围和支持 kind；这不代表全局普通/计划/未来窗口合并历史或 UI 已交付。Headless 不启动计划运行时，能力为 false，接口返回 503。

### 必需授权的一次性固定区域执行（无 Lease）

`requested_authorization.mode` 也可设为 `required`。这会创建不使用 Lease 的一次性计划，但创建时仍必须由本地用户完成选区和独立的计划创建批准。到达计划时间后，托盘宿主重新检查原 Windows SID/交互会话、显示拓扑与冻结物理区域、输出目标和最晚启动时间，再显示第二个本地执行确认；确认内容包含显示器、区域、时长、无音频和输出路径。计划创建批准本身不会启动捕获，HTTP 不能批准或绕过执行确认。

首版边界是单次、固定区域、FFmpeg 区域捕获、无音频、仅自然唤醒且交互桌面可用。应用不会唤醒机器，也不会补跑错过的实例。拒绝、超时、桌面不可用、身份/显示/输出变化或批准规格损坏都会持久化终态原因，且不会创建 Run。提交启动后若进程重启，状态为 `started_unknown`，不会重试。

通过 `GET /capabilities` 检查运行时能力：

```json
{
  "required_once_plan": {
    "setup_supported": true,
    "execution_supported": true,
    "authorization_mode": "required",
    "creation_requires_local_approval": true,
    "second_confirmation_required_at_execution": true,
    "due_occurrence_behavior": "local_per_run_confirmation_then_fixed_region_capture"
  }
}
```

只有托盘执行运行时已完成启动恢复、能够查询当前 SID/会话的到期队列，并且安全的本地确认 UI 可用时，`execution_supported` 才为 true。否则为 false；已建立的 required 计划保持 inert，setup 响应会给出 `execution_runtime_unavailable`。

在 `/api/v1/plans/{plan_id}/status` 中，required 实例还会返回 `execution_status_code`，用于区分 `pending_confirmation`、`start_committed`、`recording`、`finalizing`、`settled`、`rejected`、`expired`、`blocked`、`started_unknown`、`session_interrupted` 和 `failed`。常规 occurrence/Run 状态字段仍是公开生命周期投影：

```json
{
  "ok": true,
  "data": {
    "plan_id": "required-once-plan-...",
    "kind": "once",
    "plan_status": "enabled",
    "schedule_exhausted": null,
    "occurrence_count": 1,
    "latest_occurrence": {
      "occurrence_id": "occurrence-...",
      "window_start_utc": "2026-10-01T01:00:00Z",
      "window_end_utc": "2026-10-01T01:05:00Z",
      "status": "pending_confirmation",
      "terminal_reason_code": null,
      "run_id": null,
      "run": null,
      "output_path": null,
      "output_path_recorded": false,
      "output_file_exists": null,
      "execution_status_code": "pending_confirmation"
    }
  },
  "request_id": "..."
}
```

只有媒体结算证据通过核验后才会返回 `output_path` 并置 `output_path_recorded: true`。该模式不创建 consent Lease，也不签发 Lease-use proof。

## 5.2 每日/每周有限无人值守计划

`/capabilities.unattended_lease.recurring` 中的 `supported`、`setup_supported` 和 `execution_supported` 必须均为 `true`，且全局 `current_enabled=true`。用户必须为每个计划在本地重新选区并批准独立的周期 Lease；HTTP 只能创建或幂等恢复 setup，不能代替授权。输出目录须在请求前存在且可用。

```http
POST /plans
X-Agent-Recorder-Key: <api-key>
Idempotency-Key: <本次规范请求的稳定键>
Content-Type: application/json
```

```json
{
  "recording_spec": {
    "source": { "type": "fixed_region" },
    "audio": { "mode": "none" },
    "duration_seconds": 10,
    "countdown_seconds": 0,
    "backend": "ffmpeg-region",
    "output": { "directory": "D:\\Videos", "filename_prefix": "daily-notes" }
  },
  "schedule": {
    "kind": "daily",
    "time_zone_id": "China Standard Time",
    "local_start_date": "2026-10-01",
    "local_end_date": "2026-10-03",
    "local_time": "09:00:00",
    "weekdays": null,
    "maximum_occurrences": 3,
    "latest_start_grace_seconds": 120
  },
  "requested_authorization": {
    "mode": "recurring_lease",
    "valid_until": "2026-10-03T02:00:00Z",
    "max_runs": 3,
    "max_total_duration_seconds": 30
  }
}
```

`kind` 只能是 `daily` 或 `weekly`；`weekly` 的 `weekdays` 必须是非空、去重的小写英文星期数组，`daily` 则为 `null`。日期跨度最多 366 天、次数最多 366、单次时长 `1..600` 秒、启动宽限最多 300 秒。`max_runs` 必须等于 `maximum_occurrences`，总授权时长必须覆盖全部次数，`valid_until` 必须严格晚于最后一次计划最晚结束时刻。计划不主动唤醒机器；错过窗口、环境不符或不确定启动都不自动补录或重试。

托盘中的本地无人值守安全控制会按当前 Windows 用户和会话单独列出周期计划，并显示排期、下次本地执行、剩余 Lease 配额和活动 Run 状态。用户可在本地明确确认后撤销某一个精确 Lease。没有 HTTP Lease 撤销端点；API key 或 API 请求不能代替本地同意。

与一次性计划不同，`GET /plan-setups/{id}` 的周期响应只表示设置进度和 `scheduled` 排期，不能把 `scheduled` 当成录制成功。请查询 `GET /api/v1/plans/{plan_id}/status` 获取持久化的 Occurrence/Run 终态；成功结算时还会返回核验后的实际媒体路径及当前文件存在性。

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
  "video": {
    "fps": 15,
    "quality": "medium"
  },
  "output": {
    "directory": "default",
    "filename_template": "recording-{datetime}"
  },
  "audio": {
    "microphone": {
      "enabled": true,
      "device_id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}"
    }
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

`audio.microphone.enabled` 为 `true` 时开启麦克风录制。省略 `audio.microphone.device_id` 时自动选择单一 active 设备或单一 CoreAudio 多媒体默认设备；存在多个 active 设备且无法唯一确定默认设备时必须提供 `input_devices` 中的 ID。`audio.system_audio.enabled=true` 可直接使用当前 Windows 多媒体默认输出，也可用 `output_devices` 中的完整 ID；确认后的端点固定到本次录制，切换系统默认输出不会自动切换捕获端点，切回批准端点时执行有界恢复。麦克风与系统声音不能同时启用。

默认音频采集后端为独立 WASAPI helper。如需使用 FFmpeg dshow 诊断回退，请在启动 Agent Recorder 前设置环境变量 `AGENT_RECORDER_AUDIO_BACKEND=dshow`；其它非法值会被拒绝。

若选中设备被静音，请求会在选区/确认 UI 前失败（`409 AUDIO_DEVICE_MUTED`），应用不会自动取消系统静音；若选中设备已知为 inactive，返回 `503 AUDIO_DEVICE_NOT_AVAILABLE`。CoreAudio 状态未知时不阻断录制。

### 显示器录制请求体

```json
{
  "source": {
    "type": "display",
    "display_id": "display_1"
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
  "recording_id": "rec_xxx",
  "performance_trace_id": "trace_xxx",
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

若本机显式启用了实验性 WGC continuous window 后端，录制开始后的目标窗口关闭、最小化或尺寸变化也会明确失败，终态原因分别为 `window_closed`、`window_minimized`、`size_changed`，并通过本地失败提示告知用户。这三个值属于 recording 终态原因，不是 HTTP 请求错误码。

默认 FFmpeg 与实验性 WGC 的 display/region 路径在批准的目标显示器丢失时都会 fail closed。默认 FFmpeg 的 display/region 路径（包括分离式 A/V 变体）在可信首帧之后也会监督批准时绑定的显示器。引擎只重新枚举显示器元数据，并要求批准计划中的内部稳定显示器身份恰好匹配一个 `Resolved` 项；运行期不使用公共 `display_N` 序号、名称、坐标边界或后端自报文本作为身份。如果目标缺失、身份歧义/未解析、被替换，或 topology provider 查询失败，应用会执行强类型的内部中止，录制终态原因为 `display_unavailable`。这不是用户 Stop：不会发布最终 MP4 或 bundle，本地托盘 host 只收到一次失败通知。A/V split 会停止两个 worker 并跳过 mux；部分 worker 文件仍按受控 failed-artifact 保留策略处理。显示器拓扑恢复后，需要重新发起请求才能重新绑定。

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
  "stop_reason": "duration_reached",
  "elapsed_seconds": 300,
  "output": {
    "path": "...\\recording-2026-07-02-120000.mp4",
    "bytes_written": 1234567,
    "duration_seconds": 300.0,
    "width": 1200,
    "height": 800,
    "encoder_mode": "hardware",
    "encoder_selection_reason": "hardware_selected"
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
  "stop_reason": "duration_reached",
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

`elapsed_seconds` 说明：

- 表示从捕获开始到当前时刻或捕获结束的墙钟秒数，向下取整为非负整数。
- 录制尚未真正开始（`created`、`pending_confirmation`、`preparing`、`countdown`、`rejected`、`expired` 等）时返回 `0`。
- `preparing` 和 `countdown` 阶段保持为 `0`；这两个阶段不计入屏幕实际录制时长。
- 活动录制（`recording`、`stopping`）计算到当前时刻，会随查询增长。
- 捕获结束时状态切换到 `finalizing`，`elapsed_seconds` 冻结在实际屏幕捕获时长；合流、探测和 bundle 生成时间不会加入。
- 已结束录制计算到 `completed_at`；终态后重复查询结果稳定，不会继续增长。
- `output.duration_seconds` 是媒体产物时长（由 ffprobe 探测），两者允许因编码、取整和后端行为存在小幅差异，不要把它直接当作 `elapsed_seconds`。
- `output.path` 始终是本地用户确认后批准的最终保存目标（`Recording.OutputPath`）。成功时它指向已发布的最终媒体；失败时最终媒体不存在，`bytes_written` 为 `0`。A/V split 的 worker 临时文件、mux staging 文件和失败诊断留存文件只属于本地内部证据，不会作为公共输出路径返回。

新增字段说明：

| 字段 | 说明 |
|------|------|
| `elapsed_seconds` | 捕获开始到当前时刻（活动）或到 `completed_at`（终态）的墙钟秒数，向下取整；未开始捕获时返回 `0`。不等于 `output.duration_seconds`。 |
| `wait` | 等待信息对象 |
| `wait.requested_ms` | 请求的等待毫秒数 |
| `wait.elapsed_ms` | 实际等待的毫秒数 |
| `wait.timed_out` | 是否因超时返回（`false`=立即返回或状态变化提前返回，`true`=超时） |
| `next_poll_hint_ms` | 下次轮询建议毫秒数；`null` 表示已终止无需轮询，`1000` 表示仍在进行建议继续 |
| `stop_reason` | 终态原因：`duration_reached`（自然达到计划时长）、`floating_button`、`tray_menu`、`global_hotkey`、`user_requested` 等；仅在 `completed`/`failed` 等终止状态有意义 |
| `output.encoder_mode` | WGC 实际编码器模式：`software` 或 `hardware`；不提供该证据的后端返回空字符串。 |
| `output.encoder_selection_reason` | WGC 稳定选择依据：`software_default`、`hardware_selected`、`hardware_unavailable_fallback`、`hardware_init_failed_fallback` 或 `hardware_unverified_fallback`；其他后端返回空字符串。 |

只有 Media Foundation Sink Writer 实际使用的 H.264 transform 被验证为硬件 MFT 时，才允许返回 `hardware_selected`。能力探测发现硬件候选或请求了硬件优先模式，都不能单独作为硬编生效证据。

`since_status` 比较不区分大小写。

推荐用法：

1. 根据当前状态传 `since_status=<last_status>`，`wait_ms=25000`
2. 超时后根据 `next_poll_hint_ms` 继续轮询或再次长轮询
3. 状态变为 `completed/failed/cancelled/rejected/expired` 后停止轮询

状态：

| status | 说明 |
| --- | --- |
| `pending_confirmation` | 等待确认 |
| `preparing` | capture-safe barrier 完成；正在准备麦克风（无 REC、不捕获屏幕） |
| `countdown` | 正在显示 3-2-1 倒计时（无 REC、不捕获屏幕） |
| `recording` | 屏幕捕获中 |
| `finalizing` | 屏幕捕获已结束；正在裁剪音频、合流、探测和生成 bundle |
| `completed` | 已完成 |
| `failed` | 失败（包括 preflight 复查失败、FFmpeg 异常退出等） |
| `cancelled` | 已取消 |
| `rejected` | 用户拒绝 |
| `expired` | 确认超时 |

终态响应还会包含 `stop_reason`：

- `duration_reached`：自然达到计划时长后完成；
- `floating_button`、`tray_menu`、`global_hotkey`：用户通过本地控件主动停止；
- `user_requested`：API 调用停止且未指定具体原因，或原因空白。

用户主动停止且输出基本有效时，状态仍为 `completed`，不会仅因实际时长短于计划时长而判为 `failed`；但零时长、文件过小、FFmpeg 非零退出等真实产物错误仍会失败。

## 8.1 Chapter Marks（录制标记）

### 添加标记

```http
POST /api/v1/recordings/{recording_id}/marks
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "label": "Important decision",
  "source": "agent"
}
```

该接口是鉴权的元数据写入操作，只接受目标录制处于 `recording` 状态时的请求，不会启动、停止、批准、延长或缩短录制。`source` 可以省略（默认 `agent`），或显式传 `agent`；远程 agent 不得伪造本地 `hotkey` 来源。

成功响应会直接返回服务端接受的时间戳，不需要再次 GET：

```json
{
  "ok": true,
  "data": {
    "recording_id": "rec_xxx",
    "mark": {
      "t_ms": 1234,
      "label": "Important decision",
      "source": "agent"
    }
  },
  "request_id": "req_xxx"
}
```

约束：

- `label` 必填；服务端先检查原始提交文本的 UTF-16/Unicode scalar 结构，任何控制字符（包括首尾 CR/LF/TAB/NUL）都会拒绝，不会被 trim 掩盖。通过后才 trim 允许的外围空白；trim 后不能为空，最多 200 个 Unicode scalar value，且不会静默截断。
- `t_ms` 来自可信首帧转换时建立的私有单调 Stopwatch anchor，以 anchor 后的单调 tick delta 换算为非负整数毫秒；`StartedAtUtc` 仍是同一转换时记录的公开 UTC 元数据。它不使用请求创建、批准、后端启动、倒计时或 bundle 生成时间。首帧或单调 anchor 尚未有效建立时不会伪造标记时间。
- 标记在录制对象内按插入顺序并发安全保存；准备 bundle 时复制不可变快照，后续修改不会改变正在异步生成的 bundle。

### 本地 Chapter Marks 热键

tray 宿主在第一条录制进入 exact `recording` 时，在 WinForms UI 线程注册 `Ctrl+Shift+F11`；最后一条 exact `recording` 离开该状态时 unregister。注册失败不影响录制，能力状态保持 `registered=false`，并通过稳定审计事件记录结果；同一活动窗口不会循环重试，下一次真实的零到一条录制转换可以再次尝试。F10 全局停止热键保持独立生命周期。

热键回调在 UI 线程取得 `_activeRecordings` 中 exact `recording` 条目的稳定快照，并逐条调用与 API 相同的 `RecordingEngine.AddMark(recording_id, localized_label, "hotkey")`。一次按键可能为并发 outer/inner 都添加标记；任一条失败不会阻止其他条目，领域审计仍是每个成功标记恰好一个 `recording.mark_added`，不含 label。反馈使用现有 REC 指示器层中的短暂 badge，不调用 `NotifyIcon.ShowBalloonTip` 或 Windows shell notification；重复按键替换同一 badge/timer，不改变 stop 控件几何或焦点策略。

稳定错误：

| HTTP | code | 说明 |
|---:|---|---|
| 400 | `INVALID_ARGUMENT` | JSON、`label` 或 `source` 无效；`source` 只允许省略或 `agent`。 |
| 404 | `RECORDING_NOT_FOUND` | 目标录制不存在。 |
| 409 | `RECORDING_NOT_ACTIVE` | 目标录制不在 `recording`，响应 details 包含 `current_state` 与建议动作。 |

## 9. 停止手动录制

```http
POST /recordings/{recording_id}/stop
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "reason": "user_requested"
}
```

响应示例：

```json
{
  "recording_id": "rec_xxx",
  "status": "completed",
  "stop_reason": "user_requested",
  "output": {
    "path": "...\\recording-2026-07-02-120000.mp4",
    "size_bytes": 263781,
    "duration_seconds": 4.4
  }
}
```

## 10. 结构化录制产物（Recording Bundle）

普通本地逐次确认 video 的成功 MP4，明确后端 `wgc-continuous`（无声显示器、选区或严格窗口）及 `wgc-window-av-split`（严格窗口固定系统 loopback）也复用下述五附件 Bundle 和持久生成摘要链。既有 raw/quick/Profile/nested 请求规则、后端资格、实验开关和音频矩阵继续有效；不扩大 standing/recurring/required/future 独立执行。Bundle 异步生成，失败保留已完成 MP4。历史 GET 不哈希或补填内容，显式核验复用既有端点和预算；native 证据不可用时保持原兼容未知合同。源码验收使用合成输入与正式会话/发布链；native helper真实采集下的专项交付验收尚待进行。v0.1.16包含本能力。

上述支持范围内的FFmpeg及普通WGC MP4录制成功后，会在视频文件旁自动生成结构化产物包。例如 `D:\Videos\demo.mp4` 对应：

```text
D:\Videos\demo.bundle\
  metadata.json
  thumbnail.jpg
  first_frame.png
  last_frame.png
  marks.json
```

bundle 生成是 best-effort：即使 bundle 失败，录制状态仍保持 `completed`，原始 MP4 仍是主产物。

所有录制资源响应现在都包含顶层 `bundle` 对象：

```json
{
  "recording_id": "rec_xxx",
  "status": "completed",
  "bundle": {
    "bundle_version": 1,
    "status": "ready",
    "path": "D:\\Videos\\demo.bundle",
    "contents": [
      { "name": "metadata.json", "media_type": "application/json", "size_bytes": 1234 },
      { "name": "thumbnail.jpg", "media_type": "image/jpeg", "size_bytes": 4567 },
      { "name": "first_frame.png", "media_type": "image/png", "size_bytes": 8901 },
      { "name": "last_frame.png", "media_type": "image/png", "size_bytes": 9012 },
      { "name": "marks.json", "media_type": "application/json", "size_bytes": 120 }
    ],
    "error_code": null
  }
}
```

`bundle.status` 取值：

| 状态 | 说明 |
| --- | --- |
| `pending` | 录制尚未成功完成。`path` 为 `null`，`contents` 为空。 |
| `generating` | 主视频已通过校验，正在生成五件套。 |
| `ready` | 五件套已生成并原子发布。`path` 指向 bundle 目录。 |
| `failed` | 录制成功后 bundle 生成失败。`error_code` 为稳定错误码。 |
| `not_applicable` | 录制失败、使用 WGC 后端，或未启用 bundle generator。 |

稳定错误码：

| 错误码 | 说明 |
| --- | --- |
| `bundle_already_exists` | 目标 bundle 目录已存在。 |
| `bundle_hash_failed` | 主视频 SHA-256 计算失败。 |
| `bundle_frame_extract_failed` | FFmpeg 抽帧失败或超时。 |
| `bundle_frame_output_invalid` | 抽出的图片缺失或签名无效。 |
| `bundle_metadata_write_failed` | 无法写入 `metadata.json`。 |
| `bundle_marks_write_failed` | 无法写入 `marks.json`。 |
| `bundle_publish_failed` | 从临时目录原子发布失败。 |
| `bundle_generation_failed` | 其他意外生成失败。 |

### `metadata.json`

```json
{
  "bundle_version": 1,
  "recording_id": "rec_xxx",
  "confirmation_id": "conf_xxx",
  "generated_at": "2026-07-18T12:34:56.789Z",
  "source": {
    "type": "region",
    "title": "region:Display 1",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 100, "y": 200, "width": 1280, "height": 720 }
  },
  "recording": {
    "started_at": "2026-07-18T12:34:00.000Z",
    "completed_at": "2026-07-18T12:34:30.100Z",
    "requested_duration_seconds": 30,
    "actual_duration_seconds": 30.1,
    "fps": 30,
    "backend": "ffmpeg-region",
    "stop_reason": "duration_reached",
    "audio_microphone": true,
    "audio_capture_backend": "wasapi-helper",
    "audio_status": "recorded",
    "audio_device_id": "@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{1A2B3C4D-5E6F-7A8B-9C0D-1E2F3A4B5C6D}",
    "audio_lost_at_ms": null,
    "nested_role": "none",
    "nested_session_id": null,
    "parent_recording_id": null
  },
  "media": {
    "path": "D:\\Videos\\demo.mp4",
    "file_name": "demo.mp4",
    "container": "mp4",
    "codec": "h264",
    "width": 1280,
    "height": 720,
    "size_bytes": 1234567,
    "sha256": "64-character-lowercase-hex"
  },
  "audit_correlation": {
    "recording_id": "rec_xxx",
    "confirmation_id": "conf_xxx"
  }
}
```

`recording.audio_status` 取值：

| 值 | 说明 |
| --- | --- |
| `not_requested` | 未请求麦克风音频。 |
| `recorded` | 麦克风音频已成功写入输出，且输出中存在 AAC 音轨。 |
| `start_failed` | 请求了麦克风，但音频采集后端无法打开该设备（设备被占用、已禁用或不存在）。 |
| `lost` | 录制过程中麦克风设备丢失或断开，但输出中仍存在 AAC 音轨。 |
| `missing_audio_track` | 请求了麦克风且音频采集后端正常退出，但输出中不存在 AAC 音轨。 |

`recording.audio_continuity_status` 取值（新增）：

| 值 | 说明 |
| --- | --- |
| `not_checked` | 未执行连续性检查（例如无麦克风，或未进入 finalization）。 |
| `continuous` | 最终 AAC 音轨已检查，未检测到内部长静音。 |
| `degraded` | 最终 AAC 音轨存在内部长静音，可能发生了麦克风信号中断。`warnings` 数组会包含 `microphone_signal_interruption_suspected`。 |

`audio_status` 保持向后兼容。`recorded` 不再等同于“音频无内部缺口”；关心连续性的调用方应同时查看 `audio_continuity_status`。

麦克风录制的终态响应还会在 `audio.microphone` 中返回 `capture_strategy`、`pair_evidence`、`auto_hfp_pair_status`、`auto_hfp_pair_result_code`、`auto_hfp_pair_transport_classification`、`render_prime_ready_ms`，以及失败时的 `helper_failure_reason/stage/hresult`。完整的 current/max gap、恢复次数、gap fill 和 discontinuity 计数写入本地终态审计事件。`EstimatedGapMs` 是允许下降的当前 gauge，`MaxEstimatedGapMs` 才是单调不减的历史峰值。

### `marks.json`

`marks.json` 保持现有版本化 envelope。录制没有标记时 `marks` 仍为空数组；录制中已接受的标记按插入顺序写入，支持中文和其他 Unicode 文本。写入使用首帧时间轴上的非负整数毫秒：

```json
{
  "bundle_version": 1,
  "recording_id": "rec_xxx",
  "marks": [
    { "t_ms": 1234, "label": "重要决定", "source": "agent" }
  ]
}
```

bundle 准备时使用独立的不可变 mark 快照，不会让异步生成过程读取录制对象的活动集合。审计事件 `recording.mark_added` 只含 `recording_id`、`t_ms` 和 `source`，不含 `label` 原文。

## 11. 嵌套录制

外层录制：

```json
{
  "source": {
    "type": "display",
    "display_id": "display_1"
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

## 12. AI agent 推荐轮询策略

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

## 13. 最小使用闭环

1. AI agent 启动 `AgentRecorder.App.exe`。
2. AI agent 等待 `/capabilities` 可用。
3. AI agent 读取 API key。
4. 人类用户说：“帮我选区录屏 30 秒。”
5. AI agent 请求 `/recordings/quick`，`target.type=selected_region`。
6. 人类用户框选区域。
7. 人类用户确认录制。
8. AI agent 轮询完成并报告 MP4 路径。

## 14. 常见错误码

| 错误码 | HTTP 状态 | 说明 |
|--------|-----------|------|
| `INVALID_ARGUMENT` | 400 | 请求体或参数非法。 |
| `SOURCE_NOT_FOUND` | 404 | 目标显示器/窗口/来源不存在。 |
| `AUDIO_DEVICE_MUTED` | 409 | 选中的麦克风已被静音。应用不会自动取消静音；用户需在 Windows 声音设置中取消静音后重试。 |
| `AUDIO_DEVICE_NOT_AVAILABLE` | 503 | 选中的麦克风当前 inactive，或没有可用麦克风。建议检查设备后重试。 |
| `AUDIO_DEVICE_NOT_FOUND` | 404 | 请求中 `audio.microphone.device_id` 不存在；请重新调用 `GET /audio/devices` 获取设备列表。 |
| `AUDIO_DEVICE_REQUIRED` | 400 | 存在多个可用麦克风且无法唯一确定默认设备，必须显式提供 `audio.microphone.device_id`。 |
| `METHOD_NOT_ALLOWED` | 405 | 禁止通过 HTTP 批准/拒绝本地确认。 |

`AUDIO_DEVICE_MUTED` 与 `AUDIO_DEVICE_NOT_AVAILABLE` 都在弹出选区/确认 UI 之前返回，不会创建 recording 或 confirmation。当 CoreAudio 状态因临时故障无法读取时，状态按 unknown 处理，不会误报为静音或 inactive，也不会阻断录制。

## 15. 窗口捕获语义字段

窗口确认摘要包含以下隐私安全字段，供 agent 在用户批准前准确描述即将发生的
捕获含义：

| 字段 | 含义 |
|---|---|
| `capture_semantics` | `window_surface`（所选窗口内容，不含遮挡窗口）或 `screen_rectangle`（窗口当前屏幕矩形，可能含遮挡窗口）；显示器和区域分别使用 `display_surface` 与 `region_rectangle`。 |
| `planned_backend` | 已完成能力判断的后端名称；只表示计划，不表示已启动。 |
| `preview_semantics` | 确认预览所承诺的语义，通常与 `capture_semantics` 相同。 |
| `window_id` | 现有公开窗口身份契约；不暴露 HWND 以外的内部句柄或路径。 |
| `selection_reason_code` / `selection_fallback` | 稳定的选择原因与是否发生回退。 |

不传严格字段时，普通窗口录制保持现有 `screen_rectangle` 行为。请求真实窗口表面语义时，使用普通 `POST /api/v1/recordings` 顶层字段：

```json
{
  "required_capture_semantics": "window_surface",
  "source": { "type": "window", "window_id": "window_123456" },
  "stop_condition": { "type": "duration", "seconds": 300 },
  "audio": { "system_audio": { "enabled": true } }
}
```

该字段只接受精确字符串 `window_surface`。仅支持固定 `window_id`、普通
`video` 模式、1–1800 秒以及无音频或 system audio；不支持麦克风、`quick`、
`screenshot_series`、`nested` 或计划/Lease 请求。请求仍须本地逐次确认，HTTP
不能批准确认。确认摘要应显示 `capture_semantics=window_surface`；系统声音版本的
`planned_backend` 为 `wgc-window-av-split`，且 `selection_fallback=false`。批准后由
WGC 捕获所选 HWND 的窗口表面、由 WASAPI loopback 捕获固定输出端点，现有 A/V
finalizer 输出含 AAC 音轨的 MP4。系统声音范围是整个输出端点而非窗口独占声音。

严格窗口录制及未来窗口单次授权执行会冻结输出目录、WGC 有效暂存根和有声音时的
A/V 暂存目录，按挂载/重解析后的实际本地固定卷检查容量并按卷汇总同时存在的文件
估计。视频及 PCM 各自沿用至少 100 MiB 或每秒 2 MiB（PCM 另加 4 KiB 头部余量）；视频暂存、mux partial 和发布
副本分别计入相应卷，并留 256 MiB 安全余量。这是启动空间估计，不是磁盘预留或配额。
每次 helper/capture 启动前复核，包括未来授权的真实执行边界。

录制、合流和最终发布期间约每 5 秒检查；任一监控卷低于 256 MiB 时以
`storage_space_low` 中止，恰好 256 MiB 可以继续。卷不可用、卷身份改变、容量查询
异常/非法结果、不支持的存储或两秒查询超时使用 `storage_capacity_unavailable`。
可信中止最终为 `failed`，可通过 `stop_reason` 查询；录制未完成、未保存最终视频，
不会把短片伪装成 completed。已提交成功后迟到采样不改变结果。API 无关闭监控、
改阈值或选监控路径的开关；不会自动搬盘、删其他文件、重试或复用已消耗授权。

普通逐次本地确认的无音频 video 显示器/选区录制（raw、quick 及选区 Profile，实际选择
ffmpeg/ffmpeg-region 且无麦克风）也接通上述存储保护。实际自有 staging 与发布副本均在
批准输出目录下，启动预算为该实际卷上的两份视频估计加一次水位，不检查未使用的
WGC/AV TEMP。确认前、批准改目录后及真正 backend.Start 前复核；运行和复制期间按
尚未写入的复制字节复核，已写入字节不重复预留。本地批准改目录只替换本 Run 的布局和
monitor，不回退其他目录。容量不足或不可验证沿用上述稳定错误及 failed 终态。新增范围
不包含 Standing/Recurring/Required Plan、实验 WGC 显示器/选区、截图序列或其他
后端，不是配额或自动保留/删除；真实满盘、重挂载及桌面故障验收仍待完成。

普通逐次本地确认的有声 video，系统声音或麦克风且正式后端为
`ffmpeg-av-split/ffmpeg-region-av-split` 的 display/region，同样受存储保护。
冻结批准 output 与实际应用 AV temp，不检查未使用的 WGC TEMP。准入按实际卷身份
合并 AV temp 上的视频与保守 PCM 估计、output 上的 mux partial 与发布副本，
每卷一份 256 MiB 水位。确认前、批准后、真正音频启动前及倒计时结束视频启动前
复核，音频准备期即开始监测。最终原句柄发布按实际剩余复制字节与容量采样同步记账。
故障赢得原提交仲裁时，`storage_space_low/storage_capacity_unavailable` 停止本 Run
两个 worker 并阻止成功发布；提交已赢时迟到故障不能改写成功历史。region Profile
仍仅无音频；API 外形、音频端点绑定、同步容差和覆盖合同不变。检查只是点时判断，
不是物理空间预留、配额或保留执行；真实满盘/异卷拔除仍待桌面验收。

普通 video 的并发 Run 中，只要一方属于普通 FFmpeg AV，就不能同时占用同一规范化
最终输出路径。正式注册冲突返回 `409 OUTPUT_PATH_IN_USE`；本地批准改目录竞争失败时，
本请求变为 `rejected`，`stop_reason=OUTPUT_PATH_IN_USE`，不会启动其 worker 或停止另一 Run。
确认等待、准备、倒计时、录制及收尾期间均保留占用；终态取消也须等待自有写入清理结束。
批准改目录不能通过自动改名绕过仍占用的期望文件名，即使另一 Run 已写出 final 但尚未收敛。
比较使用本 engine 内完整路径与 OrdinalIgnoreCase。AV 每次收尾在批准 output 目录内部
生成私有 mux 文件名，复制记账、原句柄发布与 receipt 不变。这不是跨计划/宿主的持久
预留服务，不保证硬链接或目录重解析别名的所有物理同一性。

若能力不可用，严格请求在确认前失败（`WINDOW_SURFACE_UNAVAILABLE`），绝不静默
退回 `screen_rectangle`。601 秒及以上返回稳定的 `400 INVALID_ARGUMENT`，不会创建
confirmation 或启动 capture。字段非法、窗口身份/PID/尺寸改变、窗口最小化或关闭、
音频端点不可用也会明确失败。省略字段的默认窗口录制和显示器/区域能力不变。

不带严格字段时，符合现有 WGC 实验条件的受控计划仍可能使用 `window_surface`；
普通 FFmpeg 窗口路径使用 `screen_rectangle`。61 秒及其他不符合实验条件的旧请求
仍保持原有窗口矩形语义，不会被自动升级为窗口表面。

### 每条录制的开始前倒计时

两个创建接口都接受同一个顶层字段：

```json
{ "target": { "type": "primary_display" }, "countdown_seconds": 10 }
```

省略时为 `3`；只接受 `0..10` 的整数。非法类型或越界值统一返回
`400 INVALID_ARGUMENT`，quick API 会在主屏、活动窗口、选区和上次选区解析之前完成
校验。`0` 不显示数字、不写可见倒计时事件，但仍执行确认、准备、预检、捕获授权和
可信首帧转换。确认摘要会显示 `3 秒` 或 `关闭`，英文 UI 显示 `3 seconds` 或 `Off`；
录制状态通过 `config.countdown_seconds` 返回规范化值。

能力发现：`interaction.countdown` 包含 `supported=true`、`min_seconds=0`、
`max_seconds=10`、`default_seconds=3` 和 `capture_during_countdown=false`；四个
quick recipe 的 `request_template` 也包含默认 `countdown_seconds`。

用户批准后会重新执行不采集像素的计划校验。若窗口目标身份或窗口语义发生变化，
请求以 `capture_semantics_changed` 失败，并在倒计时、授权、helper/FFmpeg 启动、
REC/停止 UI 和输出文件创建之前终止。请重新创建请求；HTTP 不能批准或替换该确认。

## 有界截图序列 API


截图序列沿用 `POST /api/v1/recordings` 和 `POST /api/v1/recordings/quick`，请求体增加：

```json
{
  "mode": "screenshot_series",
  "interval_ms": 5000,
  "max_count": 12,
  "target": { "type": "selected_region" }
}
```

`interval_ms` 必须是 `1000..3600000` 的整数；`max_count` 和
`max_duration_seconds` 必须恰好提供一个。按时长计划数为
`ceil(max_duration_seconds * 1000 / interval_ms)`，超过 300 张返回
`400 INVALID_ARGUMENT`。截图序列不支持音频，任何音频请求会在设备、显示器、窗口
或选区解析前返回 `400 INVALID_ARGUMENT`。raw 请求带 `stop_condition`，或 quick 请求
带 `duration_seconds`/`stop_condition`，同样返回 `400 INVALID_ARGUMENT`，详情包含冲突
字段和建议动作；时长边界请使用 `max_duration_seconds`。

确认摘要会显示模式、间隔、有限边界、计划数量和 PNG 输出目录。批准后状态依次可为
`preparing`、`countdown`、`capturing`、`finalizing`、`completed`、`cancelled` 或
`failed`；响应中的 `output` 是目录和 `series.json`，`series` 包含计划/已捕获数量、
下一张时间和错误码。最终 `series.json` 为 UTF-8 无 BOM、snake_case、schema version 1，
帧文件名为 `frame-0001.png` 形式。截图序列不支持 marks，不能把它描述成 MP4。
时长从第一张有效 PNG 原子提交时开始计时（该帧为 `t=0`）；deadline 到达或超过时，
不会再启动下一 runner，正常完成但计划数可能大于实际数。截图 bounds、确认摘要、
runner 请求和 manifest 统一使用 `virtual_screen` 坐标空间。
每个计划点在 scheduled start 处串行认领后启动一个有限的 FFmpeg 单帧进程；不会并发
追赶。`series.json` 的第一张有效 PNG 提交是 `t=0` 锚点：`captured_offset_ms` 是有效
提交偏移，`lateness_ms` 是不含本帧捕获/编码耗时的非负认领迟到，
`capture_duration_ms` 是认领到有效 PNG 提交的单调时钟耗时。此诊断字段不构成固定桌面
毫秒延迟或实时保证。

## 未来程序窗口一次性授权

仅当 `/capabilities.future_window_one_shot.setup_supported` 与
`execution_supported` 为 `true` 时可用。该功能允许 agent 在目标窗口出现前提交待本地批准
的一次性范围；它不启动播放器，不识别直播内容，不按内容/标题挑选窗口，也不是循环计划。
所有请求都需要 `X-Agent-Recorder-Key`。

### 创建待批准授权

```http
POST /api/v1/future-window-authorizations
X-Agent-Recorder-Key: <key>
Idempotency-Key: player-session-2026-09-29-a
Content-Type: application/json
```

```json
{
  "executable_path": "C:\\Program Files\\Player\\player.exe",
  "audio": { "mode": "system_loopback", "endpoint_id": "{render-endpoint-id}" },
  "maximum_duration_seconds": 300,
  "validity_seconds": 900,
  "output_directory": "D:\\Recordings"
}
```

`audio` 必须明确选择 `{ "mode": "none" }` 或精确 endpoint 的
`{ "mode": "system_loopback", "endpoint_id": "..." }`。时长范围为 1–1800 秒，
有效期为 1–3600 秒。服务绑定本机核验的可执行文件、当前 user/session、系统 endpoint 和
固定输出文件；文件名由服务生成且冲突时失败。创建响应为 `202`，`data.result` 为
`created` 或 `existing`，授权最初为 `pending`。HTTP 永远不能批准；用户必须在本地桌面检查
完整 scope，包括“该程序所选窗口中的任何内容都可能被录制”和 loopback 录下 endpoint
全部声音的警告。相同幂等键、相同范围重放得到原授权；不同范围返回
`409 IDEMPOTENCY_KEY_REUSED`。

### 读取状态、启动一次录制

```http
GET /api/v1/future-window-authorizations/{authorization_id}
POST /api/v1/future-window-authorizations/{authorization_id}/runs
Content-Type: application/json

{"window_id":"window_123456"}
```

启动 body 只接受一个本机精确 `window_id`；客户端不能提交 HWND/PID 哈希或更改授权范围。
需由 agent 自行启动/安排播放器，并在运行时从 `/windows` 取得 ID。服务器重新枚举并要求
窗口可见、非最小化、顶层；必须能验证进程在批准后创建、user/session 一致，且恰有一个
匹配已批准可执行文件的 eligible window。并发启动只有一个能原子消费授权。成功返回
`202`：

```json
{"ok":true,"data":{"result":"start_committed","run_id":"rec_0123456789ab","status_url":"/api/v1/recordings/rec_0123456789ab"},"request_id":"..."}
```

通过 `GET /api/v1/recordings/{run_id}` 轮询运行，通过授权 GET 读取持久化状态、原因和可信
输出证据。状态可能为 `pending`、`active`、`used`、`completed`、`revoked`、`expired`、
`blocked` 或 `failed`；进程重启后无法证明开始结果时 `run_status=started_unknown`，不重试。
启动期身份、endpoint、桌面或输出检查失败均 fail closed，绝不回退到屏幕矩形。

### 撤销

用户可在本地无人值守安全控制中心查看并撤销授权。已认证 agent 也可调用：

```http
POST /api/v1/future-window-authorizations/{authorization_id}/revoke
X-Agent-Recorder-Key: <key>
Content-Type: application/json

{}
```

响应包含 `revocation_accepted` 与最新 `authorization`。撤销阻止未来启动并请求停止已消费
授权的活动 recording；被撤销的运行不会记作正常时长完成。若录制不再处于活动状态，重复
撤销不会制造新的 run 或恢复授权。需要人工复核/紧急操作时仍可从本地安全控制中心执行。

## 固定区域 Profile 管理 API

目录条目、版本页和精确版本响应中的目录名称/删除状态、当前 profile_ref、is_current 与 ETag 均来自同一个 SQLite 读取快照。规格策略字段使用领域 canonical code，例如 physical_virtual_screen、landscape_flipped、fail_if_exists、natural_wake_only、interactive_desktop_required。读取规格保留时长精度：duration_seconds 为精确 JSON 数值（整秒仍为整数，非整秒可为小数），duration_ms 为精确整数毫秒。POST/PATCH 输入仍只接受 1–600 的整数秒，不因读取精度而放宽写入权限。

`/api/v1/profiles` 是已认证的配置管理接口，仅用于查询、精确版本复制、有限字段修订和逻辑删除。当前只支持固定区域、`exact_match_only`、`ffmpeg-region`、无音频。它不创建几何、不选择显示器、不检查当前显示器可用性，也不创建录制或计划、不绑定 Profile、不授权捕获。

复制已有不可变版本：

```http
POST /api/v1/profiles
X-Agent-Recorder-Key: <key>
Idempotency-Key: <稳定客户端键>
Content-Type: application/json

{
  "name": "课件演示",
  "source_profile_ref": {
    "id": "从列表获取的 profile id",
    "version": 1,
    "digest": "recurring-fixed-region-profile/v1:<64 位小写十六进制>"
  },
  "changes": {
    "duration_seconds": 30,
    "countdown_seconds": 3,
    "output_directory": "D:\\AgentRecorder\\.local-data\\Videos",
    "filename_prefix": "lesson"
  }
}
```

`name` 必填，1–80 个可读字符；源 ref 必须精确包含 `id`、正整数 `version`、`digest`。`changes` 可省略；其唯一允许字段为 `duration_seconds`（1–600）、`countdown_seconds`（0–60）、`output_directory`、`filename_prefix`。未提供字段沿用指定源版本。拒绝重复/未知 JSON 属性、非整数、路径穿越、非法 prefix，以及任何音频、目标、选择、proof 或授权字段。源不存在返回 404，不会暗造全屏配置；服务端生成新 ID 和 v1。

列表 `GET /api/v1/profiles?limit=...&cursor=...` 按稳定 Profile ID 做 keyset 分页，默认 20、上限 100；旧内部配置也会出现，缺名称时使用确定 fallback。默认隐藏墓碑，`include_deleted=true` 可显式列出。详情返回名称、精确当前 ref、spec、删除状态及强 ETag。版本列表 `GET /api/v1/profiles/{id}/versions` 同样有界分页；`/versions/{version}` 读取指定精确版本。cursor 不透明、有限长且绑定资源，格式错误或跨 Profile 使用返回 400。

`PATCH /api/v1/profiles/{id}` 接受非空对象，只可包含 `name` 和上面的四个配置字段；每次成功都生成新不可变版本，旧版本 digest 和计划绑定不变。PATCH、DELETE 均必须带当前强 `If-Match`：缺失 428，陈旧或跨资源 ETag 412；弱、通配符或多个 ETag 为 400。成功后 HTTP `ETag` 头与 JSON `etag` 完全一致。相同 ETag 的并发 PATCH 只能一个成功；失败后先 GET 核对，不要自动重试修改。

DELETE 在同一 SQLite 写事务内检查该 ID 的**所有历史版本**引用并写入逻辑墓碑。被任一计划引用时返回 409 `PROFILE_IN_USE` 和有界引用摘要，不修改计划或授权；不会级联取消计划。墓碑阻止新修改、复制和首次绑定，历史读取及已有精确绑定 replay 保留。POST 的 Profile v1、目录行和幂等结果同事务提交；同键同规范化请求在重启后返回原结果/ETag，同键不同请求 409。

`/capabilities.profile_management` 声明 `list`、`get`、`list_versions`、`get_version`、`copy_existing_version`、`patch`、`delete` 操作；只有交互选择器与持久化 gateway 均可用时才增加 `create_from_local_selection`。`supported_targets=[fixed_region]`，headless 的 `creation_modes` 仅含复制，`audio_allowed=false`。兼容字段 `recording_or_plan_profile_ref_supported=false` 表示录制与计划组合能力并非都支持；请分别读取 `ordinary_recording_profile_ref_supported`、`ordinary_recording_execution_supported` 和 `plan_profile_ref_supported=false`。保存 Profile 本身不弹确认窗、不选区、不启动 worker、不签发 proof、不预留 Lease 配额、不创建输出目录或媒体。

首次创建固定区域配置可在支持本地选区的交互主机上调用 `POST /api/v1/regions/select`，请求 `{"purpose":"profile"}`。用户在既有本地选择器中确认区域后，响应携带原样虚拟屏幕 bounds、短期不透明 `selection_ref` 与 `expires_at`（进程内 5 分钟）。取消、超时、普通 `purpose=recording` 选择以及持久化 last-region 均不能作为该引用。该交互只保存区域位置，不代表批准或开始录制；原 `/api/v1/region-selections` 普通选择路径保持兼容。

随后用新的 `Idempotency-Key` 调用 `POST /api/v1/profiles`：`{"name":"课件演示","source_selection_ref":"sel_<opaque>","changes":{"duration_seconds":30,"countdown_seconds":3,"filename_prefix":"lesson"}}`。该创建形状与 `source_profile_ref` 复制形状互斥。`duration_seconds` 必填 1–600 秒，倒计时省略时为 3 且范围 0–10，prefix 缺省为 `recording`；`output_directory` 省略时把创建时有效默认目录固化到版本。拒绝过期/未知引用、显示拓扑/DPI/分辨率/方向变化、奇数或小于 32 像素尺寸、跨屏/越界矩形；不裁剪、不重定位、不归一化。同键同请求先按持久化幂等快照重放，再检查短期引用，因此引用过期或服务重启后仍返回原版本、digest、ETag；同键不同请求为 409。

能力字段 `creation_modes` 仅在 durable profile gateway 与本地 selector 同时可用时包含 `create_from_local_selection`；headless 仅声明复制，不声称交互选区可用。创建不创建输出目录、不做写探测、不启动 worker/媒体、不创建 Plan/Occurrence/Lease/Proof，也不产生捕获授权。创建后可按精确 ref 列表/读取/历史，并使用既有普通录制 `profile_ref` 流程；录制仍需本地逐次确认。

普通录制可通过 `POST /api/v1/recordings` 提交唯一的 `{ "profile_ref": { "id": "...", "version": 1, "digest": "..." } }` 请求体。必须使用详情/版本接口返回的精确不可变引用；不允许 raw 覆盖字段、Plan/Lease/proof、未知字段或重复 JSON 属性。目录 tombstone、摘要不匹配、版本缺失和显示环境变化均明确拒绝，不回退默认配置。只执行固定区域、exact-match、`ffmpeg-region`、无音频配置；时长须为 1–600 整秒，奇数或小于 32 像素区域以及无法精确表达的毫秒时长会拒绝，不做归一化。仍须本地用户逐次批准；批准窗可为本次更改输出目录。该 POST 没有幂等重试保证，超时后不得盲目重发。计划 Profile 引用仍不支持。

请求处理期间发现的环境不匹配会同步返回 HTTP 错误。请求一旦以 HTTP 200 接受并等待本地确认，之后（包括倒计时结束、实际启动前）发生的环境变化不会追溯改写该 HTTP 响应为 409；录制会以 `failed` 终态结算，普通状态字段 `stop_reason` 和 `error` 为 `PROFILE_ENVIRONMENT_CHANGED`。Agent 应查询录制详情或 status-wait 获取终态原因。

profile 请求错误仍使用标准 envelope，并在 `details.field`、`details.reason_code` 标明字段与原因：非法/混合/重复请求为 400 `INVALID_PROFILE_RECORDING_REQUEST`；精确版本不存在为 404 `PROFILE_VERSION_NOT_FOUND`；摘要不符、墓碑、环境漂移或后端不符为 409 `PROFILE_REF_MISMATCH`、`PROFILE_DELETED`、`PROFILE_ENVIRONMENT_CHANGED`、`PROFILE_BACKEND_UNAVAILABLE`；不可执行的 duration/geometry/policy 为 422 `PROFILE_NOT_EXECUTABLE`；快照基础设施不可用为 503 `PROFILE_EXECUTION_UNAVAILABLE`。

## 普通视频录制历史（持久化）

```http
GET /api/v1/recording-history?limit=20&cursor=<opaque>
X-Agent-Recorder-Key: <api-key>
GET /api/v1/recording-history/{recording_id}
X-Agent-Recorder-Key: <api-key>
```

这是只读、需要 API Key 的普通视频历史接口：覆盖 raw/quick、普通 nested 录制及精确固定区域 profile ref；不覆盖 unattended、standing/recurring/required-once/future-window 自动执行，也不覆盖 screenshot series。`limit` 默认 20、范围 1–100。列表按稳定 keyset 顺序分页；首屏建立 high-water，分页期间新插入的记录不会混入本轮结果。cursor 不透明且有长度上限。每次列表/详情读取使用一致的 SQLite 快照。

每项包含录制 ID、创建时间、当前/终态、来源与捕获语义、标题、请求时长、音频模式和设备快照、最终输出路径、nested 关系、精确 profile ref，以及可用的开始/结束时间、原因、产物大小和媒体时长。列表不探测媒体；详情补充记录主路径的尽力 FileInfo 元数据（存在性、字节数、写入时间），不读内容、不核验原始身份。显式 artifacts/check 才提供有界句柄式媒体/附件观察。未知详情 ID 返回标准 404 envelope；非法 limit/cursor 返回 400；存储暂不可用返回 503。

```json
{
  "ok": true,
  "data": {
    "scope": "ordinary_video",
    "items": [{
      "recording_id": "rec_123",
      "status": "recording",
      "source_type": "display",
      "capture_semantics": "display_capture",
      "audio": { "mode": "system_loopback" },
      "output_path": "C:\\Recordings\\capture.mp4",
      "nested": null,
      "profile_ref": null
    }],
    "next_cursor": null,
    "page_size": 20
  },
  "request_id": "req_123"
}
```

普通录制必须先成功同步登记，之后才会返回接受结果或启动捕获；登记失败则返回 503 且不启动采集。实际 lifecycle 变化由单一有界 writer 异步持久化，终态不可被晚到的活动快照覆盖。进程重启时，unfinished 记录结算为 `interrupted_unknown`，已完成终态保持不变。此独立历史投影不会创建或修改计划、lease、run、proof 或授权记录。能力通过 `/api/v1/capabilities.recording_history` 声明 `scope=ordinary_video`、分页范围及明确排除项。

## 统一只读任务历史

当前源码已持久保存未来窗口成功 MP4 终态与正式 publisher 的私有原始身份，按精确 authorization/run 和冻结输出绑定，在同一 SQLite 事务提交（append-only v40）。发布时 native 身份不可得为显式未知；v40 前旧行保留旧未知，不扫描或回填媒体。统一历史在既有快照内对所选行进行有界 indexed 批量验证；现代缺标记或绑定损坏沿既有 unavailable 响应失败关闭。公共字段、端点、capability、Bundle 交付、内容核验及授权范围均未增加。这是历史发布身份，不代表当前媒体字节完整性，也不是已发布 portable 包的版本公告。

用户说“查看最近做过的录制任务”“查看某段时间的任务”时，先读
`GET /api/v1/capabilities` 的 `unified_history`。App 支持 ordinary、plan、future_window；
Headless 本轮只支持 ordinary，不启动另外两类 runtime。`source=all` 是该宿主声明的
全部来源；显式指定不支持的来源会返回 400 INVALID_ARGUMENT。

```http
GET /api/v1/history?limit=20&source=all
X-Agent-Recorder-Key: <api-key>
```

按创建时间筛选：

```http
GET /api/v1/history?limit=20&source=plan&created_from=2026-10-01T00:00:00Z&created_before=2026-11-01T00:00:00Z
X-Agent-Recorder-Key: <api-key>
```

只允许 limit、source、status、profile_id、profile_version、source_type、plan_id、target_key、created_from、created_before、cursor，未知或重复参数返回 400。
limit 默认20，范围1–100；source 为 all/ordinary/plan/future_window。时间必须含秒和明确
UTC 的 Z 或零偏移（查询中的 + 要 URL 编码）；from 包含边界，before 不包含，from 必须
小于 before。不能发送 SID/session 或替其他用户选择历史。

查看普通录制中的失败任务：

```http
GET /api/v1/history?source=ordinary&status=failed&limit=20
X-Agent-Recorder-Key: <api-key>
```

status 可与创建时间组合，只允许显式 source=ordinary；搭配 all/plan/future_window 或
省略 source 返回 400 INVALID_ARGUMENT。只接受一个精确小写状态码：pending_confirmation、
preparing、countdown、recording、finalizing、completed、failed、cancelled、rejected、expired、
interrupted_unknown。空值、大写、逗号列表、任意字符串和重复键（包括 URL 编码后的重复键）
均返回400。省略 status 保持旧行为；数据库在分页前筛选，而不是过滤已经返回的一页。
响应 filters 包含 status（省略时 null）。unified_history 能力中的 status_supported_sources
只列 ordinary，supported_statuses 列出上述值；未配置 history 时两者均为空。


agent 查询“这个已保存配置录过哪些视频”时，使用持久配置 ID；查看 v2 完成记录时再加版本：

```http
GET /api/v1/history?source=ordinary&profile_id=prof_example&limit=20
X-Agent-Recorder-Key: <api-key>
```

```http
GET /api/v1/history?source=ordinary&profile_id=prof_example&profile_version=2&status=completed&limit=20
X-Agent-Recorder-Key: <api-key>
```

任一配置参数出现均要求显式 source=ordinary，Headless 也不静默改写 all；省略来源或搭配
all/plan/future_window 返回400。profile_id 按大小写敏感的持久 ID 精确匹配，不按显示名、
当前版本、改名或逻辑删除状态匹配；沿用 Profile ID 的1–128字符、无首尾空白/控制字符/
斜杠/反斜杠/冒号规则，不自动 trim/lower。profile_version 必须与 profile_id 同时使用，
采用无符号十进制正Int64；空、空白、加号、负数、零、指数、小数和溢出均400。
只有ID表示所有已记录版本，ID+版本表示精确版本；未知合法组合返回空页，不读Profile目录
判断404，也不要求配置仍存在。可组合status、创建时间、limit、cursor，条件在SQL分页前应用。
filters 新增 profile_id/profile_version，未提供时为null。items 两字段只取ordinary不可变快照，
未关联ordinary和其他来源均null；不公开profile_digest或完整配置。默认输出目录仍为.local-data/Videos。
unified_history.filters 增加两参数，profile_supported_sources 在有ordinary gateway时只列ordinary，
未配置时为空；profile_version_requires_profile_id=true，profile_match=exact_persisted_id_and_optional_version。
v26仅追加ID-only和精确版本查询的两条有序索引，不改历史内容或旧迁移。本轮不扩展观察中心
或retention参数，不代表新增引用保护或文件物理身份保护。

可选 source_type 必须显式 source=ordinary，按持久录制类别精确匹配 display、window、
region、fixed_region_profile 四个小写码。Profile 录制实际持久化 fixed_region_profile，
不与 region 合并、不改写历史；旧无类型筛选请求仍保留原先允许的其他类型。
这只是类别，不是某个窗口/显示器的目标身份筛选。空值、大写、列表、未知码、其他来源
或省略 source 均400。可组合 status、Profile、创建时间和分页；合法不相容组合为空页。

```http
GET /api/v1/history?source=ordinary&source_type=window&created_from=2026-10-01T00:00:00Z&limit=20
X-Agent-Recorder-Key: <key>
```

可选 plan_id 必须显式 source=plan，表示 Plan ID（不是 Run/Occurrence/Lease ID）。
沿用专用计划历史的1–128字符 ASCII 字母数字及 -_. 边界，大小写敏感精确匹配，
不 trim/lower/模糊匹配。可组合创建时间和分页，不能混用普通 status/Profile/source_type。
空/非法/重复参数400；未知合法ID与其他服务器 SID/session 的Plan均成功空页，不先读其
证据或泄露损坏信息。Headless 不支持 plan 来源，仍400，不伪装成功空页。

```http
GET /api/v1/history?source=plan&plan_id=plan_example&created_from=2026-10-01T00:00:00Z&created_before=2026-11-01T00:00:00Z&limit=20
X-Agent-Recorder-Key: <key>
```

两条件在 SQL 的 LIMIT 前应用，locator条件仍保留原始owner/occurrence交叉核验。
v27仅追加两个有序索引；status/Profile组合允许残余扫描，扫描成本不等于有界候选/投影数。
统一计划历史按创建时间排序，专用 /plans/{id}/history 按窗口开始时间排序，不能互相替代。
filters 增加 source_type、plan_id，省略为null；item只新增 source_type，ordinary取已有
快照，其他来源null，已有plan_id不变。不返回窗口标题、HWND、设备详情或授权身份材料。
默认保存目录仍为.local-data/Videos。保留预览继续拒绝两个新参数。
unified_history.filters 列出两个参数；有ordinary gateway时 source_type_supported_sources
为["ordinary"]，supported_source_types为上述四码；否则两列表为空。
plan_id_supported_sources仅支持plan时为["plan"]，Headless/无gateway为空。
source_type_match=exact_persisted_recording_category，
plan_id_match=exact_case_sensitive_plan_id_with_current_server_ownership。
能力发现不触发采集、迁移、设备探测或UI。

返回标准 envelope，data 包含 items、next_cursor、page_size、filters。排序是不可变创建时间
倒序，再按来源和来源内 ID 的 BINARY 升序。entry_id 分别为 ordinary:<id>、plan:<id>、
future_window:<id>，entry_kind 分别是 ordinary_video、plan_occurrence、future_window_authorization。
这是一份任务时间线：普通已接受录制、当前服务器 SID/session 拥有的三类 Plan occurrence、
以及每份 future 授权均有成员资格；未来授权尚未使用、过期或撤销也会出现，之后绑定 Run
仍是同一条授权任务。截图序列不包含在内。

可选 `target_key` 必须显式 `source=ordinary`，精确匹配冻结目标事实。格式为
`target:v1:display:<64位小写SHA256 hex>`（82字符）或
`target:v1:region:<64位小写SHA256 hex>`（81字符）或
`target:v1:window:<64位小写SHA256 hex>`（81字符）。空值、大写、格式错误、重复参数、
非ordinary来源或混用plan_id均返回400 INVALID_ARGUMENT；未知合法键返回空页。
可以组合日期、普通状态、source_type、Profile及版本。过滤在索引SQL的排序/LIMIT前完成，
每页最多100项目标事实与媒体、Bundle在同一SQLite快照批量验证，不查询当前显示器。

普通历史列表/详情与统一ordinary条目均提供nullable `target_key`，filters也返回该条件。
首次普通注册前，从不可变CapturePlan冻结一次：底层sourceKind、captureSemantics、
coordinateSpace、显式Resolved稳定显示器身份、DisplayBounds及实际归一化像素Bounds。
手动region与相同底层区域的fixed_region_profile同键；标题、后端、时长、音频、输出和
Profile版本不纳入屏幕键，旧屏幕编码不变。普通严格window_surface视频在首次持久注册前、
全局锁外从批准计划与一次只读原生观察冻结HWND位值、PID、进程创建UTC FILETIME、
Windows session ID、语义及批准表面宽高。不捕获像素，不查可执行路径/签名。
标题、移动位置、音频、保存目录和时长不改变窗口键；元数据缺失、退出或变化为null，
不会因可选历史事实未知禁止合法录制。后续审批/改目录/终态和查询复用该值，窗口关闭后也不重探测。
这是冻结事实检索键，不是Windows永久窗口UUID；同进程内HWND销毁再复用可能重现相同键。
不得将键用于授权、启动身份复核、保留或删除。legacy screen_rectangle、嵌套窗口、截图序列、
Plan/future独立历史、缺少可信事实及旧记录仍为null，不补写旧行。该键不是授权、跨电脑永久ID或当前物理身份的防篡改证明。
目标事实/schema/收据/关联损坏返回503；保留预览仍保留原有全部保护，失败前零文件探针。
`unified_history.filters`列出target_key；ordinary可用时target_key_supported_sources为
["ordinary"]，target_key_source_types为["display","region","fixed_region_profile","window"]，无gateway时均为空；
屏幕target_key_match=exact_frozen_screen_target_v1保持；ordinary可用时target_key_window_supported=true，
窗口target_key_window_semantics=window_surface，target_key_window_match=exact_frozen_window_facts_v1。

```http
GET /api/v1/history?source=ordinary&target_key=target:v1:region:aacff74f6dc6d9532b84651fad18a7bfd7e52827837b84ba52e3437c5b78e465&limit=20
# 从已有普通历史响应获取窗口键，分页保留原条件。
GET /api/v1/history?source=ordinary&source_type=window&target_key=target:v1:window:<历史中的64位小写hex>&limit=20
X-Agent-Recorder-Key: <api-key>
```

获取下一页时原样使用 next_cursor 并保留相同 source/时间/status/profile_id/profile_version/source_type/plan_id/target_key 筛选；limit 可以变化。cursor 是
最长2048字符的不透明值，绑定库/宿主身份、筛选和 schema/query 版本。首屏冻结插入水位，
晚插入的新日期和旧日期任务均不会混进后页；每页仍读取当前状态，而不是冻结所有状态。
同一服务器 SID/session 下，合法的同一迁移库重开后可以继续翻页。
新增 v24 状态索引后，旧 status 字段缺省/null 的 cursor 继续兼容；改 status 应从首页重载，
旧 cursor 不可复用。v26后旧无配置字段cursor（包括无status字段）仍可用于相同旧查询，
不能用于有配置筛选的请求；切换ID/版本或ID-only与精确版本须从首页重载，不重置旧watermark。
v27后旧无新增字段或null字段的cursor（含无status/Profile的旧阶段）仍可用于相同旧查询，
不能用于新增type/Plan筛选；切换任一新增条件必须从首页重载，兼容不丢弃cursor或重置水位。
v32后，旧targetKey字段缺省/null的cursor仍用于相同旧查询；已有目标条件的cursor不能换键或
去掉条件重放，必须从新首页开始，原owner/db/filter与watermark校验不变。
状态按每页当前快照筛选：尚未读取的任务可能不再匹配，新匹配任务若
位于已读位置之前则不能追溯；刷新首页重新建立筛选，不保证动态状态恰好一次遍历。


items 保留原始 status/reason_code，并独立提供 authorization_status、occurrence_status、
run_status、run_reason_code、execution_status_code；不适用字段为 null。recording_id、plan_id、
occurrence_id、authorization_id、run_id 也按实际来源填充，不伪造 Run 或完成时间。
只有通过该来源已有证据合同的输出才提供 output_path 和 output_path_recorded=true；
普通录制有效的主动停止可沿用真实 completed 产物。旧 Plan settled 但无路径证据仍可保持
完成状态，output_path_recorded=false。output_file_exists 单独说明文件是否存在/可访问；文件
丢失不会改写业务终态。不打开媒体内容，不输出授权 proof/nonce、拥有身份、执行程序身份、
审批摘要、Lease 配额或整份配置。

该 GET 只读，不执行授权过期写入、恢复、计划推进、确认或捕获。参数或 cursor 错误返回
400 INVALID_ARGUMENT；未接 gateway、存储/证据损坏、身份不可用、普通历史 writer 屏障超时
或失败返回 503 HISTORY_UNAVAILABLE。合法空库是成功空页，所需来源故障不能伪装成部分成功。
旧 recording-history、Plan history、授权及录制接口保持原合同。本节描述源码中的可调用接口，
不表示已发布或已完成真实桌面采集验收。

## 旧普通视频保留预览

自然语言意图示例：“看看 Recorder 数据目录 Videos 中，10 月 1 日前已完成的普通视频，当前文件占多少字节。”agent 使用以下只读 API：

```http
GET /api/v1/retention/preview?completed_before=2026-10-01T00:00:00Z&limit=20
X-Agent-Recorder-Key: <api-key>
```

这是有界只读时间规则预览，**不是安全可删除清单或删除授权**。App/Headless 使用相同实现，根在宿主接入时固定为当前数据目录的 Videos，不跟随默认输出设置。只评价有普通视频完成证据的 MP4；自选外部目录、Plan/future 成片、截图序列和 Bundle 不纳入。

只允许 completed_before（必填）、limit（默认20，1–100）和 cursor。时间必须含秒、明确 UTC 的 Z/零偏移、最多七位小数，不能晚于服务时钟；+ 须 URL 编码。年龄按实际 completed_at_utc 严格小于截止时间，不按文件创建或最后写入日期。未知/重复参数、坏 limit/time/cursor 返回 400 INVALID_ARGUMENT，不接受目录、路径、SID/session、source、execute/delete，也没有执行或清理路由。

每页只读取一次统一历史普通来源的有界候选，先复用索引 created_before 缩小范围，再按完成时间/状态/证据评价；走同一 writer 屏障和只读事务。内部读取关闭默认历史投影的 File.Exists，先校验 Videos 范围后才采样；现有公共历史行为不变。每页最多评价 limit 条，包括跳过或保护条目，不自动补读下一页。next_cursor 绑定截止时间、可信身份与宿主范围，并沿用成员水位/keyset；翻页保留截止时间，limit 可变。状态和文件样本逐页实时，不是跨页文件系统快照。

标准 envelope 的 data.items 提供公开 entry_id/recording_id、status、completed_at_utc、evaluation、reason_code、age_candidate，以及允许范围的 recorded_path、recorded_bytes 和 observed_file_bytes。evaluation 为 age_candidate/protected/skipped；age_candidate=true 仅表示旧、有效完成证据且通过 Videos/MP4 路径范围，仍可能因为文件缺失、不可访问、链接或变化而 protected。非 completed、完成时间未知、缺证据、年龄未达到、路径越界/非法/超限分别注明原因；文件缺失不改写 completed。

ordinary 嵌套保护在既有状态/完成时间/输出证据/年龄及词法路径原因之后、文件属性/大小探针之前生效：本条任一合法 nested_role、parent_recording_id、nested_session_id 非空则 reason_code=nested_relationship（父缺失、outer暂无child、自引用/循环也保护）；字段畸形为 nested_reference_invalid。自身字段均为 null 时，只要任意 ordinary 记录的 parent_recording_id 引用它，就为 nested_parent_referenced；引用者不受当前页、cutoff、状态或游标插入水位限制，不返回 child ID 或会话材料。记录页、自身字段及反向存在标志在同一 SQLite 事务快照读取，最多100项有界批量索引 EXISTS，v25只新增parent索引，旧迁移/历史列与内容不回写。关联证据缺失/查询或schema损坏沿用整页503，不默认当作无引用。

嵌套保护之后、任何文件属性/长度探针之前，若其他 ordinary_recording_history 行（排除候选自身 recording_id）、recording_run_output_evidence、required_once_execution_authorizations 或 future_window_authorizations 的明确已持久化 output_path 与候选相同，则 evaluation=protected、reason_code=shared_output_path、age_candidate=true。null 路径不匹配；引用检查覆盖不同身份、所有状态/日期、其他页及候选 watermark 之后的行，只读取私有存在布尔事实，不返回匹配来源、数量、其他 ID、proof 或 scope；不改变既有历史 owner 校验。没有引用才继续原祖先/reparse/长度检查。

shared_output_path 之后，若 standing 的 authorized_capture_scopes.output_directory+frozen_file_name、recurring 的 recurring_occurrence_execution_specs.frozen_output_file_path、required-once 的 required_once_authorized_specs.output_directory+frozen_file_name、future-window 的 output_directory+output_file_name 已具体冻结该候选文件，则 reason_code=planned_output_path、evaluation=protected、age_candidate=true、observed_file_bytes=null，在所有文件探针之前返回。匹配所有持久化冻结行，不因身份、状态、cutoff、页或水位排除；撤销/过期/结束也保守保护，不代表计划仍有效或执行已获许可。只匹配目录与文件名的组合，单独目录/文件名、未生成的模板不能匹配，不公开匹配来源、数量、计划/授权ID或证明。

standing 与 required-once API 的目录规范带尾分隔符；future-window 通常去尾分隔符、盘根保留。查询仅对候选原始文本切出文件名及父目录有尾/无尾两种有界形式（盘根两种均保留如 C:\），使用v29复合索引的两列比较；recurring 直接用冻结完整路径单列索引。复用既有版本化 OrdinalIgnoreCase collation，不LIKE、不解析全部scope/proof、不调用输出readiness、不规范化全部引用者、不读媒体/遍历目录。仅适配已有规范存储形式；非规范历史文本、别名及尚未具体冻结的用途不在无引用证明内，不能从预览推导可清理。

同一批查覆盖required_once_setup_intents.output_directory + frozen_file_name，完整精确用途复用setup_output_path；尚未选区/创建批准、无authorized spec、跨身份/会话、页外/日期外及rejected/expired等状态均保守保护。有效不同名称不匹配但仍计入全页上限。V39仅新增逐次确认setup目录/名称复合索引（两列原raw路径collation）及receipt，V1–V38定义/checksum/索引不变。四来源共用去重requested目录、同只读快照与全页500/LIMIT501，在任何探针前完成；复用类型/完整字节/NUL/字符投影检查、exact > unknown > template优先级。V39索引/receipt缺失或损坏失败关闭，读请求不修复。这是潜在输出用途，不是当前批准、授权或删除许可。

前置输出用途保护：在原 nested/shared_output_path/planned_output_path 之后、锚点/Bundle及全部文件探针之前，setup_intents.output_directory + frozen_file_name 的完整精确用途返回 setup_output_path，即使尚未生成 authorized spec。早期无输出字段 intent 不创造路径。周期 setup 的 recurring_output_directory/recurring_filename_prefix，以及 recurring_fixed_region_profile_versions 全部不可变版本的目录/prefix/template，仅对 renderer 可表达的 prefix-yyyyMMddTHHmmssZ-64位hex摘要.mp4 返回 planned_output_template。这是配置可能覆盖范围，不是已冻结文件引用、当前录制授权或执行许可；过期 intent、非latest版本及逻辑删除 Profile 仍保守保护。

只比较候选直接父目录的两种尾分隔形式及既有 raw OrdinalIgnoreCase collation；不LIKE/glob、不GetFullPath外来记录、不解析磁盘、不枚举未来occurrences。命名按有界prefix/domain及真实日期验证，支持连字符、下划线、中文及Windows大小写别名，不做Unicode规范化。其他盘、Videos2、子目录、不同prefix、无效日期/摘要及同目录普通旧文件不匹配有效模板。相关已选中固定文件名/prefix/template缺失、非法或冲突时，局部返回 pending_output_use_unknown；精确setup用途优先于局部未知，未知优先于模板，原nested/shared/frozen保护优先级不变。只公开稳定reason，不公开其他用户ID、Profile ID、配置prefix字段、用途数量或引用者清单。

V38仅新增三个目录前导索引和receipt，不改V1–V37定义/checksum/trigger、表或业务数据。去重候选目录尾分隔形式后，一次参数化UNION ALL索引批查三来源，与当前页/原关系/anchors共享SQLite只读事务。全页最多选中500输出用途，LIMIT501整页503且在任何FS probe前失败，不截断返回无用途；同目录固定setup行即使有效文件名不同也计入该保守上限。一条配置不随100候选重复计数。SQL仅投影255字符以内固定文件名、128以内prefix及200以内template；超长或非text字段在物化前变成局部未知证据。SQL投影在SQLite层、GetString前拒绝完整相关目录/文件名/prefix/template中的NUL，不能将坏后缀截短后解释为有效事实。坏关联行保留未知用途并计入500总量；仍仅用原索引raw collation选关系，选中的坏目录通过可信requested目录关联，不当作canonical证据。SQL要求text、UTF-8字节数不超过原字符上限的四倍、无NUL及原SQLite字符上限（目录含尾分隔最多1025）；后续UTF-16/domain上限不变，保留64 Rune合法Unicode前缀。schema/index/collation/receipt损坏整页503；无关目录坏行不解析。新保护项age_candidate=true、observed_file_bytes=null，原元数据预算及两个显式核验API不变。快照不承诺完整引用图、未来无用途或删除授权。

当前页、嵌套标志、四来源明确路径及四来源冻结用途标志在同一只读 SQLite 事务快照中，经既有 writer 屏障读取；最多100候选的参数化有界批量 EXISTS 使用 v25 parent 索引、v28 四个路径索引及v29四个冻结用途索引。缺失/非法私有 evidence、provider/query/schema/receipt/collation 故障整页503，不默认无引用、不提前探针、不在读请求修复。v28与v29各仅增加四索引及receipt，v1–v28既有定义/checksum、表列、触发器、业务内容不变。OperationalStore 集中建连注册固定版本 AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1，使用原始路径的纯 StringComparer.OrdinalIgnoreCase（含非ASCII大小写），不回退 SQLite NOCASE；不trim/补全相对路径/Unicode规范化/解析链接或8.3/UNC/扩展路径别名。大小写敏感目录可能保守过度保护；不同文本别名指向同一物理文件仍不在本轮证明内。

上述嵌套、共享路径或冻结用途项的 evaluation=protected；若已满足时间和词法范围，age_candidate 仍为 true，但 observed_file_bytes=null，计入 unknown_candidate_file_count、不加入 observed_candidate_bytes。时间候选计数不是可删计数，字节统计不是可释放空间。关系在快照结束后及跨页仍可变化；预览不冻结或授权未来删除。此关系层覆盖 ordinary 嵌套、四个明确路径列、四类已具体冻结用途，以及 前置setup固定输出和两类模板范围；其他尚未具体生成的计划用途不构成完整无引用证明。Bundle与物理身份沿已有后续保护判断。快照之后新增引用不在本次预览中，未来清理仍须重新验证完整引用与物理身份；未发现引用不代表无任何引用。

路径按 Windows 大小写不敏感及分隔符边界比较；相似前缀目录不算 Videos。设备/UNC/保留名称及非规范请求路径不探测。最多1024字符、从盘根起32个组件；保留盘根、Videos及各父目录的有界属性检查（最多32次），遇祖先链接/异常即保护。叶文件随后只开一个短生命周期 Windows SafeFileHandle：OPEN_EXISTING、FILE_READ_ATTRIBUTES、OPEN_REPARSE_POINT，不请求内容读取/写入/删除权限。叶最终属性、逻辑长度、链接数、删除状态、卷标识+完整128位file ID和写入时间来自同一句柄前后两次采样，不混入FileInfo.Length或第二次按路径取叶属性。每次查询basic/standard/id及最终路径；两个最终路径调用使用固定2048字符缓冲，不重试。仅明确移除系统标准\\?\盘符前缀后接受受支持的规范本地盘符路径，且两次都须与请求路径相等并满足Videos边界。

attribute_checks_per_entry_max=32 限定上述祖先按路径属性调用；叶文件的两次句柄basic属性查询是另有固定上限的调用。

新增稳定原因：hard_linked_output（可靠链接数>1）、file_identity_unavailable（身份/元数据缺失、非法、不支持或native失败）、file_identity_changed（删除中或同次卷/身份/属性/长度/link count/写入时间变化）、resolved_path_mismatch（最终路径无法可靠取得、截断、非本地形式、不等或越界）。叶reparse/目录、缺失、权限等明确情况沿用既有原因；采样后变成reparse/目录或负长度仍为metadata_changed。上述不可信样本observed_file_bytes=null并计入未知字节；稳定且合法、唯一链接、路径相符才用同句柄长度比较，可靠file_length_changed仍保留当前长度与统计。缺失/非法provider或结果契约沿用整页503，普通native文件采样失败按项protected，不向HTTP/日志返回卷ID、file ID、句柄、其他解析目录或native异常原文。

共享仅允许读、不提供写/删除共享，但纯属性访问可绕过数据句柄的共享冲突，也不能钉住文件名或祖先链。不宣称排除全部并发/内存映射写入；固定调用与缓冲次数不是同步native调用的硬墙钟或取消保证，不建超时后台线程。探针不解码、不读内容/hash、不遍历目录、不写媒体或启动进程。

evaluated_count 是本页历史候选数，age_candidate_count 是通过年龄及证据/路径范围的数目。observed_candidate_bytes 只合计本页年龄候选的可靠 MP4 逻辑长度；observed_candidate_file_count/unknown_candidate_file_count 和 observed_bytes_complete 区分已采样与未知，未知不伪装成0。记录长度不符时 protected，但可靠当前长度仍可单独报告和合计。这不是整个目录总量、Bundle体积、磁盘实际分配或可释放空间。

缺服务、调用方身份/数据库/证据错误、writer 屏障失败/超时或字节溢出返回整页 503 RETENTION_PREVIEW_UNAVAILABLE，不回成功空页/部分页；文件级缺失或保护保留在条目评价中。错误只返回白名单，无凭证和任意异常原文。详细字段与 reason_code 白名单见 docs/api.md 的 Retention preview 节。

capabilities.retention_preview 以及每页均明确 read_only=true、deletion_supported=false、reference_protection_ready=false、physical_identity_protection_ready=false、bundle_included=false，并公布范围及分页/路径上限。普通 completed 输出仅接受共享发布器原始 CREATE_NEW 副本句柄签发的私有收据：WGC continuous 最终发布、AvFinalizer 有音频 mux 和已验证的 None 最终发布（包括 AV split 和默认 FFmpeg 无音频视频终态路径）传递收据；AV 中间视频、FFmpeg legacy 直接音频、截图序列尚未锚定；计划/future 成片各自保留私有身份，但不纳入 ordinary 保留预览。旧记录不回填，预览观察不会转成发布 ID。 Windows 物理发布器以写入、读属性及 DELETE 访问打开原始副本，分享读/删除而不分享写。既有 gate 内按原句柄 rename/replace；副本已改名时仍提交被验证的原对象，不移动旧名上的替换文件。失败时先对原句柄设置 disposition 再关闭；句柄无效或 disposition 失败则保守留残，不重开/删除旧路径。原对象确实提交后的可选身份故障可以无收据，提交失败不回退到路径移动；这不证明父目录链或提交后的外部变化原子安全。

本页 ordinary 行、嵌套/shared/planned 引用及私有锚点在同一只读 SQLite 快照获取，锚点最多100项批量索引读取。既有时间/词法/引用原因之后，未锚定返回 protected / publication_identity_missing、observed_file_bytes=null，在祖先及叶探针之前保护并计入未知字节。已锚定且 Bundle 已登记 not_applicable 时，仍执行原祖先、单句柄、硬链接、稳定性及解析路径检查，再比较卷与完整128位 file ID；任一位不同返回 publication_identity_mismatch，字节未知。身份匹配后才比较可靠当前长度，保留 file_length_changed 的可靠字节统计。锚点 schema/编码/关联损坏沿用整页安全503，不只读修复，也不公开身份材料。

在原时间/完成证据、词法、nested/shared/planned 及缺失发布锚点保护之后，普通 Bundle 状态在任何祖先属性、叶句柄及文件存在探针之前保守保护 MP4：

| 持久 Bundle 事实 | evaluation / reason_code |
| --- | --- |
| null（未登记） | protected / bundle_evidence_unknown |
| generating | protected / bundle_generation_in_progress |
| ready | protected / bundle_artifact_reference |
| failed 或 interrupted_unknown | protected / bundle_artifact_state_unknown |
| not_applicable | 继续原祖先、单句柄、身份和长度规则 |

上述四类保护仍保留 age_candidate=true、recorded_path/recorded_bytes；observed_file_bytes=null，计入 unknown_candidate_file_count，不加入 observed_candidate_bytes。复用同一只读 SQLite 快照中已批量读取、最多100项的 Bundle 事实，不另开逐项连接、不读 JSON/图片/MP4 内容、不遍历或按目录补证据。状态/路径/错误组合、媒体绑定、schema/收据或 provider 契约损坏仍整页503，不能被前置保护原因掩盖；旧历史不回填，仍为 null。

Bundle 理由之后、runtime 和任何文件探针之前，另一 ordinary 主 MP4 锚点或 future-window/plan 主 MP4 锚点保存相同原始二进制 volume/file 身份时，返回 protected / shared_publication_identity。不同路径、历史大小、SID/session、状态、日期、cutoff、页或 cursor 水位均不限制引用；只以精确 BINARY 语义排除 ordinary 候选自身 recording_id，其他来源同字符串 ID 仍匹配，覆盖 standing/recurring/required_once 三种计划。不返回外来路径、ID、owner、来源、数量或证明材料。

最多100项已验证候选锚点，在原只读事务内用一个 BLOB 参数化批次及三个身份索引 EXISTS 的 OR 读取私有关系事实；SQLite V42 仅新增三个索引及迁移收据。缺失/错误表结构、索引、collation、收据或不完整/错误关系结果均在完整页任何文件探针之前整页503 RETENTION_PREVIEW_UNAVAILABLE，不能被旧保护理由或 runtime 阳性掩盖。GET 不迁移/修复，V41 输入须经正常宿主初始化。候选缺原锚点保持未检查及 publication_identity_missing，不补身份。

命中保留本条 age_candidate/path/recorded_bytes，observed_file_bytes=null 计入未知、不增加实测字节；无匹配继续原 runtime/物理观察。外来坏业务字段不会将匹配 tuple 过滤成无引用；未命中的坏/未知键不宣称已全面验证。命中只证明保存的原始身份引用，不证明两个路径当前指向同一对象，ID复用可能保守过度保护。五附件、任意 worker、磁盘别名、未来引用及删除 TOCTOU 未覆盖；DB/runtime/FS 是不同时间点，不新增 pin、完整无引用、安全删除结论或删除授权。endpoint、分页、read_only/deletion_supported 和不完整引用 readiness 保持原语义。

App 与 Headless 每页还从该宿主实际 RecordingEngine 获取一次不可变 ordinary 主 MP4 claim 时间点快照。最多请求100个通过年龄/词法范围校验的不同路径；原输出锁最多等待50ms，已有登记最多检查500个 owner。第501条、关停、缺provider、锁超时、异常或坏返回均整页503 RETENTION_PREVIEW_UNAVAILABLE，且在任何祖先/属性/叶探针之前失败；重复、缺失或外来返回路径也是契约损坏。空历史或无年龄/范围候选可不查询。仅按已登记 canonical 文本和 OrdinalIgnoreCase 精确匹配，不补目录、解析别名、枚举磁盘或扫描全部录制。

原 nested/shared/planned/pending、缺发布锚点及 Bundle 原因保持优先；剩余匹配路径返回 protected / runtime_output_in_use，保留 age_candidate=true 及自身 recorded_path/recorded_bytes，observed_file_bytes=null，计入未知而不增加实测字节。登记仍存在时，无论 Stop 已返回、逻辑 Completion 或 completed/cancelled/failed 状态都包含；读请求不退休 owner，不访问后端或等待 OutputSettled。不公开 owner ID、数量、来源或 nonce。HTTP 仍只接受原 cutoff/limit/cursor，不新增任意路径输入或端点。

覆盖仅限本宿主已有 ordinary video 主输出登记，包括无声、有声/AV split 及原登记已覆盖的 ordinary WGC；不宣称覆盖 Bundle/input/hash-reader/temp 或跨进程占用。DB 和运行期是两个时间点，之后新 claim 不在本次观察内。无匹配不等于无引用或可删；没有 pin、未来排他或删除闸门，read_only=true/deletion_supported=false 保持。

ready 只证明历史关联，不能担保当前附件物理完整；failed/unknown 不能证明没有部分产物。not_applicable 只表示记录了本次不生成 Bundle，不证明不存在其他引用或可删除。事实在事务后或下一 cursor 页可变化，不能冻结未来删除决策。五项附件名称、类型和生成字节可作为持久历史清单读取，但尚无逐附件物理身份或完整跨产物引用图。bundle_included 继续 false：附件实体和体积未纳入预览，完整附件引用/身份/删除保护尚未完成；read_only=true，删除/reference/physical readiness 继续 false。

age_rule_match 仅增加有限的发布副本身份比较，不证明上游内容/视频真实性或排除全部同账户进程。原子祖先链、采样之后的变化、ID复用、原位同大小改写、内存映射写入、全部别名和删除TOCTOU仍未解决；完整引用、权限和明确本地删除授权仍需另行设计。本能力不等于完整保留策略；受限单项删除另见相应章节。

### 普通视频 Bundle 的持久历史事实

`GET /api/v1/recording-history`、`GET /api/v1/recording-history/{recording_id}` 及 `GET /api/v1/history` 的 ordinary 条目增加可选 `bundle`：未记录的旧历史及其他统一来源为 `null`，已记录保留 `status`、`path`、`error_code`，增加 nullable 的生成附件清单。

```json
{
  "bundle": {
    "status": "ready",
    "path": "D:\\captures\\clip.bundle",
    "error_code": null,
    "contents_evidence_version": 1,
    "contents": [
      {
        "name": "metadata.json",
        "media_type": "application/json",
        "size_bytes": 1234,
        "content_hash": null
      },
      {
        "name": "thumbnail.jpg",
        "media_type": "image/jpeg",
        "size_bytes": 5678,
        "content_hash": null
      },
      {
        "name": "first_frame.png",
        "media_type": "image/png",
        "size_bytes": 9012,
        "content_hash": null
      },
      {
        "name": "last_frame.png",
        "media_type": "image/png",
        "size_bytes": 3456,
        "content_hash": null
      },
      {
        "name": "marks.json",
        "media_type": "application/json",
        "size_bytes": 78,
        "content_hash": null
      }
    ]
  }
}
```

状态为 `generating`、`ready`、`failed`、`not_applicable`、`interrupted_unknown`。媒体 completed 与 Bundle failed 可以同时成立，Bundle 更新不改写 MP4 终态、大小、时长、输出路径或私有发布锚点。媒体收尾在同一事务登记 generating/not_applicable，后续仅接收该录制冻结媒体和生成操作的内部结果。失败只保存白名单稳定错误码，不保存 stderr 或异常全文。ready 表示既有生成器报告的历史生成结果，不保证查询时目录完整、原对象身份或附件可删除；查询不读 Bundle 图片/JSON、不扫描目录重建事实。

有界排空后重启仍可查询结果。正式宿主启动恢复把遗留 generating 记为 interrupted_unknown，错误码 bundle_interrupted_unknown，并撤销旧操作，迟到回调不能恢复 ready；不自动再生成、不恢复录制或授权。保持既有鉴权、过滤、分页、cursor 和媒体元数据合同；写失败或读屏障/排空不完整沿用 unavailable，媒体 completed 不等于 Bundle 更新已落库。保留预览仍只读，删除/reference/physical readiness 仍 false。


普通历史 `bundle.contents_evidence_version` 仅在首次 ready 结果原子登记上述完整五项时为1，`contents` 为固定顺序五项；名称/媒体类型和正数 size_bytes 来自正式 generator 验证过的自有暂存输出，在目录发布前冻结、发布成功后才返回。不支持额外项或任意路径。这是生成时字节，不是查询时文件采样、内容哈希、当前物理身份或可删除额度。

旧 ready、没有明确清单的兼容 generator，以及 generating/failed/interrupted_unknown/not_applicable 两个新字段均为 null，不是假装已证实空集合；原未登记 Bundle 仍为 bundle:null。v33 增量迁移不读取或回填旧附件。新增必要登记丢失、残缺清单、错误版本/计数/类型/尺寸或录制/媒体/目录/operation 绑定损坏沿原 unavailable/503，不降级为旧未知。首次登记及 ready 状态/清单同事务提交；同 operation 同值重放幂等，冲突或终态迟到补清单拒绝。TryPublishBundle 的队列接受不等于持久成功。恢复只撤销生成中操作，不重新生成，已有 ready 清单重启后保留。

三个历史投影在原 SQLite 读快照批量读取最多100项普通媒体/500项附件，与媒体、目标键、Bundle 及引用一致，不扫描目录或读取附件。鉴权、参数、target 筛选及 cursor 不变。capabilities.unified_history 根据普通服务公布 bundle_contents_supported_sources=["ordinary"]、bundle_contents_evidence_version=1、bundle_contents_max=5、bundle_contents_semantics="recorded_generation_bytes_not_current_file_identity"；未服务普通历史时支持来源列表为空。ready 仍被保留预览保护，损坏先于文件探针失败；bundle_included=false 和删除/reference/physical readiness=false 保持，记录大小不加入实测可回收字节。


每项普通历史 `bundle.contents` 新增 nullable `content_hash`，可信时恰好四字段：`{"algorithm":"sha256","value":"<64 小写 hex>","basis":"bundle_publication","size_bytes":9012}`。五项摘要覆盖正式 Publisher 实际成功复制进原始附件对象的完整字节，复用原固定缓冲；Flush、连续 original holds、整目录无覆盖提交及提交后身份核对全部成功后才签发完整集合。私有证据绑定 recording、exact operation、媒体/Bundle 路径、固定 ordinal/name/size 和各项原始物理身份，在 SQLite V37 与 Bundle 状态、contents、identity 及独立主媒体摘要同事务保存。

V37 不扫描文件、不回填旧行，metadata 内已有 hash 也不能补齐可信摘要。迁移前事实、公共 `Ready(...)`/仅清单结果、未绑定操作或缺原生身份保持 `content_hash: null`；新行缺必需标记、损坏/缺项/跨绑定集合 fail closed。普通列表、详情和 unified ordinary 从同一 SQLite 快照 indexed batch 读取，最多100个父项/500项附件，以第501行检测越界，不读取附件内容；其他 unified 来源没有此普通 Bundle 证据。

`bundle_publication` 是生成发布时历史观察，不代表截图与视频共享时刻、捕获原始字节、附件当前完整性、可播放、未来完整性保证或删除授权。附件按需内容比较见下文；`artifacts/check` 仍仅元数据，`verify-media` 仍仅主 MP4，1 GiB/30秒/单 worker 合同不变。保留预览仍只读并保护 ready Bundle。


### 普通产物按需可用性检查

```http
GET /api/v1/recording-history/{recording_id}/artifacts/check
X-Agent-Recorder-Key: <key>
```

App、Headless 均接通，只适用于 ordinary video。**不接受任何 query 参数**、路径、附件名或修复指令。未带/错误 key 为401（UNAUTHORIZED/FORBIDDEN）；未知参数或非法ID为400 INVALID_ARGUMENT，无记录404 RECORDING_HISTORY_NOT_FOUND，历史/证据损坏或不可用503 RECORDING_HISTORY_UNAVAILABLE，均先于文件探针。未配置 gateway 时接口503、capability.supported=false。普通列表与统一历史只读DB；详情另补主文件尽力元数据，不自动调用本句柄式检查。

经过原 writer 读屏障，在一个只读SQLite事务冻结单个ID的媒体、Bundle清单和可选私有媒体发布锚点，不读live录制状态、不初始化/恢复/补证。completed、ready和登记大小仍表示过去生成事实，不随当前检查改变。接口不写历史/schema、不读取文件内容或目录成员、不执行工具进程/捕获。

保持 `{ok,data,request_id}` envelope，data字段如下：

| 字段 | 含义 |
|---|---|
| `recording_id` | 请求的普通历史ID。 |
| `check_started_at_utc`, `check_completed_at_utc` | 含DB工作的UTC开始与响应快照时间。 |
| `probe_state` | complete/busy/timeout/cancelled/failed；complete仅表示处理完成，不表示文件全部正常。 |
| `media_history_status` | 冻结的 pending_confirmation/preparing/countdown/recording/finalizing/completed/failed/cancelled/rejected/expired/interrupted_unknown；尚未取得快照则null。 |
| `media` | name固定为media的单项检查，不返回输出路径。 |
| `bundle.history_status` | 冻结的 generating/ready/failed/interrupted_unknown/not_applicable；未登记Bundle或未取得快照则null。 |
| `bundle.contents_evidence_version` | 明确完整生成清单为1，否则null。 |
| `bundle.contents` | 已知ready/version1时为五项检查；未知则null，不能解释为已证实空集合。 |
| `read_only`, `metadata_only` | 均为true。 |
| `content_verified`, `deletion_supported` | 均为false。 |
| `observation_semantics` | sequential_point_in_time_metadata_not_atomic_snapshot。 |

每项均含 `name`、nullable `recorded_size_bytes`、nullable `observed_size_bytes`、`observation`、`reason_code`、nullable `sampled_at_utc`、`identity_state`。五项固定按 metadata.json、thumbnail.jpg、first_frame.png、last_frame.png、marks.json 排序，路径只由已校验冻结Bundle目录推导。sampled_at_utc为该项元数据尝试开始UTC，null表示未尝试；中断项可有时间但没有完成叶采样。逐项观察不代表六项在同一物理时间点。

| observation | 含义 |
|---|---|
| present_size_matches | 稳定元数据长度匹配登记大小，身份另看identity_state。 |
| missing | 明确文件/祖先不存在。 |
| size_mismatch | 稳定实测长度不同。 |
| inaccessible | 拒绝访问或共享冲突，不能当作缺失。 |
| unsafe_path | 词法、reparse、目录冒充或解析路径不符而拒绝。 |
| changed_during_probe | 同句柄两帧元数据/身份变化或待删除。 |
| unavailable | 不支持、畸形、异常、中断尝试，或路径/祖先检查超界。 |
| not_checked | 未完成检查，如timeout/cancel后剩余项或媒体不适用时附件。 |
| not_applicable | 媒体不是已完成普通MP4。 |
| unknown | 已完成MP4缺少可用完成/正长度证据。 |

identity_state在稳定媒体或五项附件与各自可信私有发布锚点比较时为match/mismatch；无锚点的有效采样为unverified，无法判定为unknown。正式Bundle producer的完整五项原始对象身份集合随ready及清单在SQLite V35同事务持久化；旧历史、公共Ready(path)、仅清单兼容结果及原生身份不可用仍未锚定，不扫描、不回填。已锚定附件被同名同大小替换时，长度可匹配但身份为mismatch；新身份集合损坏或缺项导致历史读取失败，不能静默降级为unverified。媒体、清单与附件锚点从同一数据库快照冻结，文件观察仍只是各采样时间点的元数据核验。match也不证明内容未改、可播放、音画完整或可安全删除。不公开卷/file ID、锚点、operation ID、nonce或异常全文。

reason_code完整白名单为：

- not_checked、media_not_applicable、media_evidence_unknown；
- path_invalid、path_check_limit、ancestor_check_limit、local_drive_unavailable；
- ancestor_reparse_point、parent_not_directory、parent_missing、file_missing、file_inaccessible；
- leaf_reparse_point、output_not_file、metadata_changed、metadata_unavailable、resolved_path_unavailable、resolved_path_mismatch；
- publication_identity_mismatch、file_size_mismatch、metadata_and_publication_identity_match、size_matches_identity_unverified；
- probe_busy、probe_timeout、probe_cancelled、probe_failed。

非completed视频/证据不足时零文件IO。旧ready/未知/generating/failed/interrupted_unknown/not_applicable清单始终nullable，不根据现存五文件补证。仅规范本地盘路径，允许Videos以外的合法用户自选输出；UNC/设备/ADS/相对/遍历/保留名称在属性和叶打开前拒绝。沿用1024字符上限、**整请求最多64个不同祖先**；祖先属性仅本请求复用，无跨请求缓存。reparse祖先/叶和目录冒充均拒绝。每个叶复用Windows单句柄READ_ATTRIBUTES、share-read元数据探针，对比前后属性、大小、身份和最终路径，不打开内容句柄。

每宿主/进程共享一个worker额度，无等待队列。**总deadline为3秒，从DB投递前开始**。busy返回未检查结果；timeout/cancel冻结部分结果：已结束采样保留，进行中项为unavailable，后续项not_checked，取消后不启动下一项。同步原生/SQLite调用可能尚未退出，额度持续占用至实际退出，API关闭及服务/API重建也不能释放旧占用。返回对象和数组不会被工作线程随后改写，不借用旧请求结果。API关闭显式取消请求；可确认的连接错误/RST尽力触发取消。发送方向FIN不表示客户端放弃接收，单凭EOF不能可靠区分优雅Close与发送半关闭；无法区分时仍受原总deadline约束，不增加等待周期。

capabilities.ordinary_artifact_check公布 supported、endpoint、scope=ordinary_video、max_files=6、max_distinct_ancestors=64、deadline_ms=3000、metadata_only=true、content_verification=false、deletion_supported=false、observation_semantics=sequential_point_in_time_metadata_not_atomic_snapshot、identity_scope=media_and_five_bundle_publication_anchors。描述当前源码能力，未表示已发布版本。DB快照与随后文件观察不是原子快照，祖先链未原子锁定，采样后变化、同大小原地写入、ID复用、别名及目录TOCTOU不能据此排除。保留预览继续保护ready Bundle，未知字节及deletion/reference/physical readiness不变，本检查不能授权清理或绕过引用保护。

附件身份match不改变保留保护：原Bundle保护仍先于物理采样，不能自动获得删除权限。本接口不公开收据或身份材料，不执行内容哈希或播放检查。


### 普通媒体生成阶段持久摘要（源码 V36）

`GET /api/v1/recording-history`、其 `/{recording_id}` 详情的 `data.recording`、以及统一 `GET /api/v1/history` 的 ordinary 条目新增顶层 nullable `media_hash`：

```json
{"media_hash":{"algorithm":"sha256","value":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","basis":"bundle_generation","size_bytes":1048576}}
```

该值记录成功 Bundle 生成阶段观察到的主 MP4 字节。内部接线要求正式普通 MP4 媒体发布收据（FFmpeg路径及上述普通WGC路径）、已登记 recording/Bundle operation，以及同一实际读句柄上的读前/读后原生路径、物理身份、长度和属性核对。复用原来一次 SHA-256 流式读取，固定 64 KiB 缓冲并支持取消，读取期间拒绝共享写入/删除；成功后与 Bundle 状态、清单和身份事实同事务保存，并与本次 metadata.json 的 media.sha256 一致。重新读 metadata.json 不能签发可信摘要。

旧历史、没有或失败的 Bundle、公共假 Ready 结果、无法取得可信原生绑定均返回 null。V36 不回填、不扫描媒体；历史查询沿现有一致 SQLite 快照按索引批量读取，最多 100 项，不读取媒体或附件内容。新登记缺标记、摘要类型/算法/hex/长度损坏及媒体/操作绑定错误使查询不可用，不降级为可信值；其他统一来源为 null，不增加 hash 筛选。当前内容比较必须显式调用下述 POST 核验端点。

capabilities.recording_history.media_hash_supported 与 capabilities.unified_history.media_hash_supported_sources 声明 ordinary 支持，media_hash_algorithm=sha256、media_hash_basis=bundle_generation。摘要不代表捕获原始字节、当前完整性或可播放；哈希之后的变化、三次独立抽帧及附件内容不在此证据内。artifact-check 继续 metadata_only=true、content_verification=false；retention_preview 仍只读并保护 ready Bundle，摘要不是删除授权。本功能包含在 v0.1.16 中。

### 普通主视频显式内容核验

`POST /api/v1/recording-history/{recording_id}/artifacts/verify-media` 使用现有 loopback、已配置 API key 和当前宿主隔离。请求体只能为空或 `{}`，查询参数及任何对象字段（路径、算法、期望 hash、预算等）均在派发前以 `400 INVALID_ARGUMENT` 拒绝；ID 沿用普通历史校验。不存在返回 `404 RECORDING_HISTORY_NOT_FOUND`，无配置或损坏事实返回 `503 RECORDING_HISTORY_UNAVAILABLE`，不泄露原生异常、物理 ID 或额外路径。

既有 envelope 的 data 含 recording_id、check_started_at_utc、check_completed_at_utc、check_state、comparison、reason_code、bytes_read、recorded_hash、observed_hash、content_verified、scope=main_media、read_only=true、deletion_supported=false、observation_semantics=single_read_handle_point_in_time_comparison_not_future_integrity_guarantee。recorded_hash 为生成阶段持久保存的 algorithm/value/basis/size_bytes；完整稳定读取时 observed_hash 为 algorithm/value/size_bytes，算法固定 sha256、value 为64位小写hex。未知或部分读取的 observed_hash 为 null。

check_state 为 completed/busy/timeout/cancelled/failed，comparison 为 match/mismatch/not_checked。只有完整有界读取、前后原生元数据及路径稳定、匹配原始物理身份、生成摘要相等时 content_verified=true。原对象同长度原地修改会返回 digest_mismatch/mismatch；同长度替代对象或大小变化分别为 publication_identity_mismatch/file_size_mismatch，comparison=not_checked，零内容读取。无可信摘要返回 recorded_hash_unavailable，不打开媒体；未完成或非MP4为 media_not_applicable。

其余稳定 reason_code 包含 publication_anchor_unavailable、media_evidence_unknown、media_byte_limit、path_invalid、path_check_limit、ancestor_check_limit、ancestor_reparse_point、parent_not_directory、local_drive_unavailable、file_missing、file_inaccessible、unsafe_or_unknown_handle_metadata、metadata_unavailable、read_length_mismatch、metadata_changed、verification_failed、verification_busy、verification_deadline、verification_cancelled。它们描述本次尝试，不表示永久健康状态。部分、不稳定、取消及超时结果均为 not_checked、observed_hash=null、content_verified=false；bytes_read 是响应时冻结的进度。未取得可信事实前超时或无摘要时 recorded_hash 可为 null。

capabilities 新增独立 ordinary_media_verification：supported、endpoint、scope=main_media、algorithm=sha256、reference_basis=bundle_generation、max_bytes=1073741824、deadline_ms=30000、max_concurrent=1、read_only=true、deletion_supported=false 及上述观测时间语义。截止从数据库派发前开始，包含实际句柄核对和释放。整个宿主最多一个 worker、不排队；超时或取消返回后仍挂起的 SQLite/native/读操作继续占用额度，真正退出才回收，重建 service/gateway 不能绕过。确认连接错误和宿主关闭关联取消；仅发送方向 FIN 不足以证明对端放弃接收，仍由总截止约束。

同一 SQLite 快照冻结终态、Bundle、摘要和原锚点，无 V37、迁移、回填或修补。既有叶文件以 OPEN_REPARSE_POINT 只读打开，仅共享读；内容读取前在该实际句柄核实最终规范路径、长度、属性及完整物理身份。固定64 KiB缓冲流式读取，尾读不超过冻结剩余长度，最多1 GiB。ADS/UNC/device/reparse/目录/硬链接及未知原生证据保守拒绝，祖先最多64项；祖先不是原子锁，不声称消除目录TOCTOU或未来ID复用。

结果仅比较本次句柄观察到的主媒体字节与 bundle_generation 摘要，不证明捕获原始字节、可播放、音画完整、附件完整、未来持续一致或安全删除。结果不持久保存，不调用 FFmpeg/ffprobe、播放、捕获或人工确认。旧三个历史投影、GET artifacts/check、retention preview 不触发核验；旧 ordinary_artifact_check 继续 metadata_only=true/content_verification=false，ready Bundle 与引用保护不变。


### 显式按需核验五项 Bundle 附件（源码 V37）

`POST /api/v1/recording-history/{recording_id}/artifacts/verify-attachments` 将固定顺序五项 metadata.json、thumbnail.jpg、first_frame.png、last_frame.png、marks.json 当前字节与持久 `bundle_publication` SHA-256 比较。带 `X-Agent-Recorder-Key`，请求体仅空体或 `{}`。即使普通 auth 关闭也必须配置并提供 key，沿当前 loopback/宿主隔离。所有 query 和 JSON 字段（包括重复字段、路径、名字、算法、hash、预算）均在 DB 派发前拒绝。参数/ID非法400，不存在404，无配置或损坏事实503，不泄露原生异常。

只有 ordinary completed MP4、ready Bundle、完整可信 V37 五项 hash/身份集合才进入读取。`ReadArtifactFacts` 同一 SQLite 读事务冻结终态、contents、摘要和发布物理锚点。旧/未知/非ready/不适用返回 not_checked，不从目录、metadata 或附件重建证据，不回填 DB；不新增迁移，仍 V37。

**整请求**上限五文件、64个去重祖先、**64 MiB（67,108,864字节）及30秒**，包含 DB 派发、祖先/native、读取和实际关闭。可信长度 checked 累加超限先于内容 open 拒绝。沿已有 canonical 本地路径/drive policy，拒绝 UNC/device/ADS、reparse、非目录祖先/非文件叶、硬链接、删除待决或未知 native 事实。逐项一个 OPEN_REPARSE_POINT/OVERLAPPED、FileShare.Read 实际句柄，64 KiB缓冲完整SHA-256；读取前核对原身份和长度，替换/长度不符不hash。读取后同句柄事实、实际Position/Length及关闭完成后才确认该项 observed_hash。

附件与 `verify-media` 共享**宿主总并发1、无队列**，双向busy，重建service/gateway不能绕过。超时、明确断线/RST、host stop取消；单向FIN保持既有语义。挂起DB/native/read的超时/取消请求继续占名额，到实际句柄全部关闭、worker退出才释放。主MP4原1 GiB/30秒预算保持。

响应含 recording_id、UTC check_started_at_utc/check_completed_at_utc、check_state（completed/busy/timeout/cancelled/failed）、comparison（match/mismatch/not_checked）、reason_code、总bytes_read、items、content_verified、scope=bundle_attachments、read_only=true、deletion_supported=false 和 observation_semantics。事实尚未取得时 items可为空，取得后固定五项顺序。每项仅 name、check_state/comparison/reason_code、bytes_read、可空UTC观察开始/结束、recorded_hash（algorithm/value/basis=bundle_publication/size_bytes）、完整稳定时的observed_hash（algorithm/value/size_bytes）及content_verified；不公开额外路径、物理ID、operation、receipt、异常文本。

digest_match表示该项完整稳定读取匹配；digest_mismatch区分身份相同/同长原对象内容改写。publication_identity_mismatch、file_size_mismatch、unsafe_or_unknown_handle_metadata、file_missing、file_inaccessible、read_length_mismatch、metadata_changed等不能确认内容。第一项非匹配后停止，已关闭完整前缀观察可保留，后续项not_checked；不能用前缀或可访问当整集合成功。超时/取消冻结返回进度，后台后来结束不改变已返回结果，未完成项不返回prefix observed_hash。仅五项全匹配、关闭且未超过截止/取消时整请求content_verified=true/comparison=match。

这些是**顺序读句柄各自时间点观察，不是同时/原子五文件快照**，前项在响应时可能已变化；不代表共享截图时刻、可播放、未来完整性或删除授权。与主MP4端点组合也不是原子六文件快照。history GET、retention和旧artifacts/check不触发内容核验，旧check继续metadata-only。

capabilities.ordinary_attachment_verification单独声明 supported/endpoint/scope=bundle_attachments、algorithm=sha256、reference_basis=bundle_publication、max_files=5、max_distinct_ancestors=64、max_bytes=67108864、deadline_ms=30000、max_concurrent=1、concurrency_scope=host_content_verification、shared_with指向verify-media、read_only/deletion_supported及顺序观察语义。本节能力包含在 v0.1.16 中。

示例：共享名额占用、尚未读取事实时返回：

```json
{"recording_id":"rec_example","check_started_at_utc":"2026-10-12T01:00:00Z","check_completed_at_utc":"2026-10-12T01:00:00Z","check_state":"busy","comparison":"not_checked","reason_code":"verification_busy","bytes_read":0,"items":[],"content_verified":false,"scope":"bundle_attachments","read_only":true,"deletion_supported":false,"observation_semantics":"sequential_read_handle_point_in_time_comparisons_not_simultaneous_or_atomic_snapshot_earlier_files_may_change_before_response_not_future_integrity_guarantee"}
```

### 计划成片的原始发布身份

standing、recurring 和 required_once 成功 MP4 在原赢家事务内保存正式最终 publisher 的原始 receipt；native 身份仅内部保存，旧记录或合法无法取得 native 身份的成功仍为未知。计划详情、分页历史和统一 source=plan 历史在原只读快照内批量验证选中事实，保留 512/100/100 上限。现代缺失或损坏沿既有 unavailable/503 合同返回经过清理的错误。公开 DTO、cursor、端点和 capability 不变；历史 GET 不读取媒体字节、不计算摘要、不回填身份。原始身份只表示发布时事实，不能证明当前字节、可播放性或删除权限。本能力已通过独立验收，包含在 v0.1.16 中。


### 计划与未来窗口主视频检查（工作区源码，未发布）

`GET /api/v1/history/{entry_id}/artifacts/check` 必须带 API key，仅支持当前 SID/session 的 `plan:<occurrence_id>` 与 `future_window:<authorization_id>`，只检查主 MP4。把完整 entry ID 编码成一个 URI segment，且只编码一次；周期 ID 自带的 `recurring-occurrence/v1:<64位小写hex>` 前缀按其固定格式接受，任意分隔符、剩余百分号或双重编码都不能变成其他 ID/路径。无 query、无 body；ordinary 继续原 recording-history 入口。

401 为鉴权失败；400 `INVALID_ARGUMENT` 为格式/来源/参数不支持；404 `HISTORY_NOT_FOUND` 为不存在或身份/会话不符；503 `HISTORY_UNAVAILABLE` 为 gateway 缺失或可信事实损坏。错误不返回 SQL、路径、原生身份或 proof/nonce，均先于媒体探测。

精确 indexed lookup 在一个只读 SQLite 快照核对原 owner、目录索引、生命周期、冻结输出、成功大小与 V40/V41 私有身份；新 facts 不触发旧存在性投影、不 flush writer、不初始化/迁移/恢复/补写。standing/recurring 即使 native anchor 未知也从验证后的 V41 取成功大小；required_once/future 与自身结算交叉核对。现代 marker/关联/类型/原 TEXT NUL/receipt/DDL 损坏失败关闭。未完成或媒体证据不足为 not_checked/unknown；历史大小未知不冒造，合法旧/原生未知有已知成功大小时 identity=unverified。

返回 envelope 的 data 包含 entry_id/source、UTC 起止、probe_state、media_history_status 和 media（name、recorded_size_bytes、observed_size_bytes、observation、reason_code、sampled_at_utc、identity_state）。无路径、原始 volume/file ID 或 Bundle。固定 scope=main_media、read_only=true、metadata_only=true、content_verification=false、bundle_included=false、deletion_supported=false。complete 只表示处理完毕；缺文件不改历史 completed。

新旧 metadata 入口共享宿主一个实际 worker、不排队；3 秒包含数据库派发及资源释放、最多64祖先，新入口最多1叶句柄且拒绝多链接。超时/明确断线/宿主停止冻结不可变响应，真实工作退休前继续占额度，busy 不读 DB/文件。两类30秒内容核验保持独立，旧 ordinary 六文件 DTO 不变。READ_ATTRIBUTES 元数据句柄可与某些数据写者并存，share-read 不构成内容写入锁。

capabilities.history_artifact_check 给出 gateway supported、supported_sources 两种来源、endpoint、main_media、max_files=1、max_distinct_ancestors=64、deadline_ms=3000、max_concurrent=1、host_metadata_check 和共享 ordinary 入口及固定 flags。缺 gateway 时 sources 为空。Headless 可只读历史 Plan/future，不扩展捕获授权，其统一分页 ordinary-only 仍保持。

observation_semantics=sequential_point_in_time_metadata_not_atomic_snapshot：数据库事实与后续文件采样是两个时间点边界，不读内容、不列目录、不启动进程/捕获，不证明声音完整、可播放、未来不变、无引用或可安全删除。本能力已通过独立验收，包含在 v0.1.16 中。

### 单项删除申请（SQLite V44，受限范围）

默认App录制仍登记Bundle，生成失败也不是not_applicable，继续受保护。新ordinary请求可显式选择本文开头的仅MP4政策，正式publisher保存主媒体原锚点及not_applicable历史，再按原删除范围和独立本机批准处理。已有历史不重解释；合成输入的正式发布/原生处置独立检查不等于真实桌面或人类批准验收。

ready 的交互 App 已接实际本机确认协调器；Headless、未接线或未 ready 时 `capabilities.retention_deletion.supported=false`，申请/查询返回稳定 `503 RETENTION_DELETION_UNAVAILABLE`，0 UI、0 native。两个入口均要求已配置的 API key，不能利用其他路由的可选鉴权设置绕过。API key 不能批准删除。

`POST /api/v1/retention/deletion-requests` 的 UTF-8 body 仅允许 `{"recording_id":"rec_example"}`，并要求单个 `Idempotency-Key` header。键去除首尾空白后为1–128个可打印 ASCII 字符，不含空白、斜杠、反斜杠、逗号。body 上限2048字节、recording ID上限200字符；未知/重复字段、重复header、错误类型、NUL、路径、query和多余参数返回 `400 INVALID_ARGUMENT`。owner、固定 Videos 根、engine/store/writer和真实UI由host冻结，不能通过请求提供许可、批准或外部operation。

`202` 表示持久申请已建立，不表示已批准或删除。标准envelope的data含 `deletion_request_id`、`recording_id`、`request_state`、`local_confirmation_state`、`persisted_request_phase`、`query_url` 及下述结果。直接跟随 `query_url`：`GET /api/v1/retention/deletion-requests/{id}`，不带body/query。请求ID必须是32位小写hex，编码分隔符、多段ID被拒绝；不存在/外来请求统一 `404 DELETION_REQUEST_NOT_FOUND`。已知请求即使结果未知也返回200；持久事实损坏、busy、host停止/未ready为503，按 `suggested_action` 查询原申请，无需让人类转述诊断。

任何弹窗之前，原子CreateOrGet持久绑定可信owner、键摘要、规范请求摘要与随机请求ID，数据库对象实际退出后才等待人类。同键同body返回原申请，同键异body为 `409 IDEMPOTENCY_KEY_CONFLICT`。并发/HTTP断线重试不能产生第二个窗或动作，HTTP断线不拥有也不回收协调器。宿主总额度为一个pending/approved/executing流程、无队列；worker/Close未知继续busy。确需新尝试时必须新key和新本机批准；不能通过自动换key重试未知结果。

本机独立确认窗说明文件名、完整可复制路径、大小、完成时间、永久删除、不进回收站和保留历史；默认Enter/Esc/关闭/超时取消。真实本机批准且UI实际退休后，原短期Permit才能使用。API没有批准/取消入口，对话中的“确认”也不能代替本机批准。执行前先持久提交唯一approval关联并退出请求DB对象，再进入原V43 prepared账本及原句柄处置，保留两段60秒、准备/保护共同3秒和退休后独立结算3秒。请求DB操作及精确查询各有包含清理的总3秒预算；查询只有一个实际worker，超时/取消后继续占额度直到实际退休。

`request_state` 为 `awaiting_local_confirmation`、`execution_reserved`、`finished`、`confirmation_invalidated` 或 `unknown`，finished仅指流程结束。`local_confirmation_state` 独立为 pending/approved/rejected/expired/cancelled/unavailable/busy/invalidated。无实际关联V43操作时 `operation_id`、`disposition_progress` 为null，不能编造未执行结果。有操作时处置为 not_attempted/accepted/rejected/unknown，并提供有限 `reason_code`、可空 `win32_error`。`file_action_performed=true` 只代表OS接受原句柄删除标记，unknown为null，不代表物理删除完成或空间回收，不返回deleted或回收字节。

`original_resources_retired`、`ledger_resources_retired`、`our_resources_retired`、`request_resources_retired` 分开描述实际退休，无法证明时可为null。`preparation_commit`、`ledger_settlement`、`request_write_commit` 为 committed/not_committed/unknown，无操作时前两项可为null；`persisted_disposition_progress` 单独保留实际settled账本事实。请求最终写入失败不能抹去accepted。新进程只读旧申请：未批准的本地确认失效，已执行预约但无结算事实仍未知；同key只返回旧申请，0 UI/0 native，不恢复Permit、不继续删除。请求SQLite事务与Windows处置不是跨系统原子事务，预约后账本missing不能推断未动作。

范围仅当前固定Videos直接子级的单个ordinary completed主MP4、原可信发布锚点/成功大小、明确not_applicable Bundle、现有限定用途保护通过。不支持目录/路径、批量、自动清理/配额、Plan/未来窗口/附件、未知旧文件，不重新采集身份来授予权限。独立 `retention_deletion` capability公布两路由和scope、requires_local_confirmation/single_item/permanent/no_trash=true、unattended=false及恢复/结果语义。原 `retention_preview` 继续read_only、deletion_supported=false、reference_protection_ready=false、physical_identity_protection_ready=false；不表示完整引用图、全命名空间冻结或safe_to_delete。私有UI自动化自测不等于真实桌面确认窗或发布包验收。
