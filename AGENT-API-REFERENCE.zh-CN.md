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

`capture.first_frame_observed` 仅覆盖默认 FFmpeg 视频链路（`display`/`window`/`region`）。它会在 FFmpeg `-nostats -progress pipe:1` 输出报告 `frame >= 1`、`total_size > 0` 且进度组正常结束（`progress=continue` 或 `progress=end`）时产生一次。该事件证明“FFmpeg 已报告至少处理一个视频帧且输出流已有正字节数”，是“本地用户批准 → 首个可观测编码/复用进度”的时延上界，**不是**屏幕采集精确交付第一帧的时间、不是物理磁盘首帧写入时间、也不是 MP4 可播放或输出校验通过的证据。如果 FFmpeg 没有产生满足条件的 progress 组，该事件可能缺失。

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

用途：确认服务已启动，并读取是否支持 `display`、`window`、`region`、嵌套录制和确认机制。

该接口不需要 API key。

**WGC continuous 边界**：仓库内包含实验性原生 `wgc-native-helper.exe`、托管会话与 capture backend 适配器；受控 selector、非捕获能力探测、短期成功缓存和 FFmpeg 自动回退已接通，并通过一次受监督的 10 秒 3840×2160 产品路径录制。**WGC 连续显示器录制仍未作为公共 API 能力开放**，默认关闭且尚未进入 portable 包。公共请求不能直接指定 WGC continuous；普通 agent 应继续按本文档使用公开的 display/window/region 能力。

**音频能力**：麦克风默认由隔离的 Windows WASAPI helper 捕获，最终合流编码为 AAC；FFmpeg dshow 仅作为显式诊断回退。蓝牙 Hands-Free 输入会被动识别传输类型，并自动发现同一设备容器的渲染端点，通过静音 render prime 建立并保持 HFP 双工链路。AirPods Pro 与 Focal Bathys 已通过真实产品路径验收，但不同设备、固件和驱动仍可能失败；失败会进入明确终态，不会发布静音成功视频。终态响应和审计包含 capture strategy、配对证据、render-prime 延迟、current/max gap、恢复和 discontinuity 诊断。`recording.audio` 保留为兼容性数组，现在报告 `["microphone"]`。`recording.audio_capabilities.microphone` 在设备枚举成功且存在至少一个 active 输入时返回 `{ "supported": true, "status": "ready" }`，无设备时返回 `{ "supported": true, "status": "no_devices" }`，枚举失败时返回 `{ "supported": true, "status": "unavailable" }`。`system_audio` 仍为 `{ "supported": false, "status": "not_implemented" }`。请求中设置 `audio.system_audio.enabled=true` 会返回 `CAPABILITY_NOT_IMPLEMENTED`。

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
- 如果 `context.windows.active == null`，应让用户聚焦窗口或改用 `selected_region`
- 如果 `context.last_selected_region == null`，不要假设存在上次选区；可改用 `selected_region` 先让用户选区
- 显示器/窗口枚举失败时，`/capabilities` 仍返回 200，错误信息在对应 `error` 字段中

### 就绪文件（推荐）

服务启动成功后还会写入 `<data-dir>\runtime\ready.json`，AI Agent 可以轮询该文件判断服务就绪，无需盲轮询 `/capabilities`。

## 1.1 检查权限

```http
GET /permissions
```

不需要 API key。`screen_capture` 与 `output_directory` 为本地授予。麦克风已支持，其 `status` 诚实反映设备可用性：`available`（存在可用设备）、`no_devices`（无设备）或 `unavailable`（枚举失败）。本版本不探测真实 Windows 麦克风 ACL，因此不报告 `granted`。`system_audio` 仍为未实现。

```json
{
  "screen_capture": { "status": "granted" },
  "microphone": { "supported": true, "status": "available" },
  "system_audio": { "supported": false, "status": "not_implemented" },
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

不需要 API key。通过 FFmpeg dshow 枚举真实麦克风输入设备，并为每个设备附加新鲜的 CoreAudio 只读状态。`status` 为 `ready`（存在设备）、`no_devices`（无设备）或 `unavailable`（枚举失败）。`microphone_supported` 为 `true`；`system_audio_supported` 为 `false`。

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
  "system_audio_supported": false
}
```

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
| `target.type` | 是 | `primary_display` / `active_window` / `selected_region` / `last_region` |
| `target.selection_timeout_seconds` | 否 | 仅 `selected_region` 生效，默认 `120`，范围 `10..600` |
| `duration_seconds` | 否 | 转换为 `stop_condition: { type: "duration", seconds: n }`；不填则手动停止 |
| `video` | 否 | 透传到原始录制配置，默认值同原始 API |
| `audio` | 否 | 透传到原始录制配置；`audio.microphone.enabled` 为 `true` 时开启麦克风录制。省略 `audio.microphone.device_id` 时自动选择：仅有一个 active 设备、或仅有一个 CoreAudio 多媒体默认设备时选中该设备；否则必须提供 `audio.microphone.device_id`（来自 `GET /audio/devices`）。`audio.system_audio.enabled` 必须 `false` 或省略。若选中设备被静音，请求会在选区/确认 UI 前失败（`409 AUDIO_DEVICE_MUTED`）；若选中设备已知为 inactive，返回 `503 AUDIO_DEVICE_NOT_AVAILABLE`。应用不会自动取消系统静音；状态未知时不阻断 |
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

`audio.microphone.enabled` 为 `true` 时开启麦克风录制。省略 `audio.microphone.device_id` 时自动选择单一 active 设备或单一 CoreAudio 多媒体默认设备；存在多个 active 设备且无法唯一确定默认设备时必须提供 `audio.microphone.device_id`（来自 `GET /audio/devices`）。`audio.system_audio.enabled` 必须为 `false` 或省略，`true` 会返回 `CAPABILITY_NOT_IMPLEMENTED`。

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

成功的 FFmpeg MP4 录制完成后，会在视频文件旁自动生成结构化产物包。例如 `D:\Videos\demo.mp4` 对应：

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
| `not_applicable` | 录制失败、是 WGC still-frame PNG，或未启用 bundle generator。 |

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

当前仅定义版本化空结构，`marks` 数组为空，待后续实现鼠标/键盘标记后填充。

```json
{
  "bundle_version": 1,
  "recording_id": "rec_xxx",
  "marks": []
}
```

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
| `CAPABILITY_NOT_IMPLEMENTED` | 400 | 请求的能力尚未实现，例如 `audio.system_audio.enabled=true`。 |
| `AUDIO_DEVICE_MUTED` | 409 | 选中的麦克风已被静音。应用不会自动取消静音；用户需在 Windows 声音设置中取消静音后重试。 |
| `AUDIO_DEVICE_NOT_AVAILABLE` | 503 | 选中的麦克风当前 inactive，或没有可用麦克风。建议检查设备后重试。 |
| `AUDIO_DEVICE_NOT_FOUND` | 404 | 请求中 `audio.microphone.device_id` 不存在；请重新调用 `GET /audio/devices` 获取设备列表。 |
| `AUDIO_DEVICE_REQUIRED` | 400 | 存在多个可用麦克风且无法唯一确定默认设备，必须显式提供 `audio.microphone.device_id`。 |
| `METHOD_NOT_ALLOWED` | 405 | 禁止通过 HTTP 批准/拒绝本地确认。 |

`AUDIO_DEVICE_MUTED` 与 `AUDIO_DEVICE_NOT_AVAILABLE` 都在弹出选区/确认 UI 之前返回，不会创建 recording 或 confirmation。当 CoreAudio 状态因临时故障无法读取时，状态按 unknown 处理，不会误报为静音或 inactive，也不会阻断录制。
