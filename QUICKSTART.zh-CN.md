# Agent Recorder 快速开始

Agent Recorder 是一款 **AI agent 原生录屏能力层**。常规路径是：人类用户说出录屏需求，本地 AI agent 调用 quick API，Agent Recorder 弹出本地选区/确认 UI，最后输出 MP4。

## 如何使用

1. 下载并解压 Windows portable zip。
2. 让本地 AI agent 阅读：
   - `AGENT-INSTRUCTIONS.zh-CN.md`
   - `AGENT-API-REFERENCE.zh-CN.md`
3. 对 AI agent 说一句自然语言指令，例如：

```text
帮我选区录屏 30 秒。
```

或：

```text
帮我录制当前对话窗口 5 分钟。
```

4. AI agent 应该负责：
   - 运行 `AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json`
   - 从返回的 `api_key_file` 读取 API key
   - 优先调用 `POST /api/v1/recordings/quick?wait_for=recording&wait_ms=25000`
   - 以 `wait.goal_reached=true` 作为可信开始信号
   - 有界等待超时后，再继续长轮询录制状态
   - 等待录制完成
   - 录制完成后报告 MP4 输出路径和元数据

5. 人类用户只需要：
   - 在弹出的选区界面中拖拽框选，或点击高亮窗口直接选择其区域
   - 根据需要移动、缩放或输入精确坐标；边缘默认自动吸附，按住 `Alt` 可临时关闭吸附
   - 在本地确认窗口中检查录制信息，可按需更改保存目录，然后明确点击「确认」批准录制
   - 播放 AI agent 返回的视频文件

## quick API 目标类型

| target.type | 说明 |
| --- | --- |
| `primary_display` | 录制主显示器 |
| `windows_display` | 按 Windows“标识”显示的正整数录制指定显示器 |
| `active_window` | 按当前活动窗口的可见边界录制 |
| `selected_region` | 弹出选区 UI，让用户框选区域后录制 |
| `last_region` | 复用最近一次成功选区 |

请求示例：

```json
{
  "target": { "type": "selected_region", "selection_timeout_seconds": 120 },
  "duration_seconds": 30,
  "countdown_seconds": 3,
  "video": { "fps": 30, "quality": "medium" }
}
```

`windows_display` 的 `windows_display_number` 必须是 Windows“标识”界面显示的正
JSON integer，例如 `2`，不是 API `display_id` 的数字后缀；成功后仍需本地确认。

有界创建等待只观察本地批准、准备、倒计时和可信首帧，不会通过 HTTP 批准录制。
返回 `timed_out=true` 时，使用返回的 `recording_id` 继续
`GET /recordings/{id}` 长轮询；省略 `wait_for` 时保持原有的立即创建响应。

`countdown_seconds` 对 raw API 和 quick API 都可选，省略时为 `3`，只接受 `0..10`
的整数。设为 `0` 可关闭可见倒计时，但仍保留本地确认、准备、预检和可信首帧门槛。
该值会在本地确认摘要及录制响应/状态的 `config` 中展示。

## 文件位置

通过 portable 包中的 `AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json` 启动时，默认 data-dir 是 `<package-root>\.local-data`，文件位置为：

- API key：`.local-data\config\api-key.txt`
- 录制文件：`.local-data\Videos\`
- 审计日志：`.local-data\logs\audit.jsonl`

如果直接运行 `AgentRecorder.App.exe` 或 `AgentRecorder.Headless.exe` 且未设置 `AGENT_RECORDER_DATA_DIR`，默认 data-dir 是 `%LOCALAPPDATA%\AgentRecorder`。AI agent 应以 `ensure-running` 返回的 `data_dir` 和 `api_key_file` 为准。

## 安全边界

- API 默认只监听 `127.0.0.1`。
- 状态变更接口需要 `X-Agent-Recorder-Key`。
- AI agent 可以请求录制，但不能静默录制。
- 每次录制都必须由本地用户确认。
- HTTP 自批准接口被阻止，返回 405。

## 停止录制

录制进行中时，托盘图标会变为红色并显示录制状态。可以通过以下三种方式停止：

1. **悬浮停止按钮**：每个录制区域右上角会出现红色小按钮，点击仅停止该条录制。
2. **托盘菜单**：右键托盘图标，选择「停止录制」（单条）或「停止全部录制（N）」（多条）。
3. **全局热键**：按 `Ctrl+Shift+F10` 停止全部活动录制。

普通录制中，REC 边框和悬浮停止按钮对用户保持可见，但会尽量从录制视频中排除。嵌套录制时，符合安全几何条件的 inner 控件会保留在 outer 视频中，以完整记录内层录制过程。当录制区域外侧空间不足时，停止胶囊会自动移到该显示器内侧，不改变捕获排除策略。

AI agent 也可以通过 API 停止指定录制：

```http
POST /api/v1/recordings/{recording_id}/stop
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "reason": "user_requested"
}
```

## 添加章节标记

录制中按 `Ctrl+Shift+F11` 可添加章节标记。Agent Recorder 会短暂显示绿色应用内提示，不使用托盘气泡；如果 outer 和 inner 同时录制，一次按键会按各自的首帧时间轴分别添加标记。

AI agent 也可以通过鉴权 API 为一条活动录制添加带名称的标记：

```http
POST /api/v1/recordings/{recording_id}/marks
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "label": "重要决定"
}
```

成功完成 FFmpeg MP4 录制后，已接受的标记会写入 `<视频文件名>.bundle/marks.json`。

托盘菜单提供中文/English 语言选择，设置保存在本机，并应用于后续打开的选区、确认和录制控制窗口。

## 公开系统声音

系统声音已经作为公开能力提供，并与所有录制一样需要本地确认。quick 请求可以直接使用
当前 Windows 多媒体默认输出：

```json
{
  "target": { "type": "selected_region" },
  "duration_seconds": 30,
  "audio": { "system_audio": { "enabled": true } }
}
```

raw 请求使用相同的 `audio` 对象，例如：

```json
{
  "source": { "type": "display", "display_id": "display_1" },
  "stop_condition": { "type": "duration", "seconds": 30 },
  "audio": { "system_audio": { "enabled": true } }
}
```

省略 `audio.system_audio.device_id` 时使用当前 Windows 多媒体默认输出；如需显式指定，
请使用 `GET /api/v1/audio/devices` 返回的 `output_devices` 中的完整 `id`，列表只包含
active render endpoint。单次录制不能同时启用麦克风和系统声音。本地确认窗批准的端点在
本次录制期间保持固定，切换 Windows 默认输出不会改录其他设备；切回批准端点时执行有界
恢复，静音缺口会在连续性元数据中如实报告。

## 有界截图序列

截图序列仍使用同一个 quick 接口，并且必须由本地用户确认：

```json
{
  "target": { "type": "selected_region" },
  "mode": "screenshot_series",
  "interval_ms": 5000,
  "max_count": 12,
  "countdown_seconds": 3
}
```

响应会返回 `mode: "screenshot_series"` 以及计划/已捕获数量。最终产物是包含编号 PNG
和 `series.json` 的目录；音频和章节标记不适用。若使用时长边界，改用
`max_duration_seconds`，不能与 `max_count` 同时发送。raw 请求不要发送
`stop_condition`，quick 请求不要发送 `duration_seconds` 或 `stop_condition`；这些字段
会在目标和音频解析前以 `400 INVALID_ARGUMENT` 拒绝。时长从第一张有效 PNG 提交时
开始计时，第一张为 `t=0`；deadline 到达后正常结束，实际数量可以少于计划数量。
截图区域和 manifest 的坐标空间固定为 `virtual_screen`。
计划点按顺序认领，每个点启动一个有界 FFmpeg 单帧进程，不会并发追赶。manifest 的
`lateness_ms` 只表示计划点认领时的非负迟到，不包含本帧捕获/编码耗时；新增的
`capture_duration_ms` 表示从认领到有效 PNG 提交的单调时钟耗时。它们是诚实的诊断字段，
不是固定桌面毫秒延迟承诺。

## 发布包里有什么

```text
AgentRecorder.App\                 应用主体
AgentRecorder.Headless\            无交互 UI 的高级服务宿主
AgentRecorder.Cli\                 agent 启动握手工具
AgentRecorder.AudioHelper\         隔离的 Windows WASAPI 音频 helper
README.zh-CN.md                    中文说明
QUICKSTART.zh-CN.md                本文件
AGENT-INSTRUCTIONS.zh-CN.md        AI agent 操作指令
AGENT-API-REFERENCE.zh-CN.md       API 快速手册
LICENSE                            Agent Recorder MIT 许可证
LICENSE-NOTICE.md                  第三方许可说明
```
