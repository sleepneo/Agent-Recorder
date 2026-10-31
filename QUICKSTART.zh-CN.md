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

## 如何录制声音

**不指定声音时，默认只录画面，不录声音。** “录制整个窗口”不表示自动录下该窗口播放的声音。需要音频时，直接告诉 AI agent：

```text
找到 PotPlayer 窗口，录制整个窗口 20 秒，开启系统声音，不录麦克风。
选区录屏 15 秒，开启 AirPods 麦克风，不录系统声音。
录制主屏 15 秒，不录任何声音。
```

- **系统声音**：录制所选输出设备上的全部声音，其他应用也可能录入，不是窗口独占音轨。
- **麦克风**：普通选区和显示器视频支持；窗口内容录制不支持。
- **不能同时开启系统声音和麦克风。** 固定区域配置/无人值守及截图序列仍不录声音。
- 未指定设备时，使用当前 Windows 多媒体默认输出或可唯一确定的麦克风输入；本地确认后设备固定，切换默认设备不会自动改录另一设备。
- 批准前核对确认窗中的声音模式及设备；请求的音频不可用时，应由 agent 明确告知，不能悄悄改录静音视频。

完整规则见 [README 的声音录制说明](README.zh-CN.md#声音录制规则)。

## 嵌套录制测试

先对 AI agent 说：

```text
开始 outer 选区录制 120 秒。
```

完成外层选区和本地确认，看到 REC 后，立即发送：

```text
在 outer 继续录制期间，开始 inner 选区录制 20 秒。
```

再完成内层选区和本地确认。120 秒外层时长为 agent 响应及两次交互留出余量，
不是固定的响应时间保证。若要在 outer 视频里看到 inner 的录制控件，请在同一
显示器内选择范围，使 inner 区域及控件位于 outer 范围中。

检查 inner 结束后 outer 是否仍在录制，以及是否分别得到两段正常视频。
若创建 inner 前 outer 已结束，agent 应明确告知本次未形成嵌套，不能按嵌套成功报告。

## 一次性无人值守测试

先完成准备，再开始计时。可以先对 AI agent 说：

```text
检查有限无人值守是否已开启；若未开启，告诉我在哪里启用，先不要创建计划。
```

若尚未开启，在 Recorder 托盘菜单打开无人值守安全控制，由你在本地启用。
请让 agent 再次检查当前实例的开关状态；确认已开启后，再发送：

```text
两分钟后自动录制一个固定区域 10 秒，无音频；现在让我选区并批准一次性授权，到点不要再次询问。
```

“两分钟后”以准备完成后这次新指令为准，不包含等待启用开关的时间。
完成选区并在本地批准这一次授权，agent 应报告具体的计划开始时间及排期结果。
启用全局开关不等于已批准具体录制；等待开关或批准时，也不能报告已排期。

测试期间保持 Recorder 运行、电脑唤醒且桌面未锁定。检查到点是否无需第二次确认
就开始录制，并在约 10 秒后结束。若批准超时或错过执行窗口，应明确报告未执行，
不能自行延后或重复录制。

## quick API 目标类型

| target.type | 说明 |
| --- | --- |
| `primary_display` | 录制主显示器 |
| `windows_display` | 按 Windows“标识”显示的正整数录制指定显示器 |
| `active_window` | 固定活动窗口身份并捕获窗口自身内容，排除遮挡窗口 |
| `selected_region` | 弹出选区 UI，让用户框选区域后录制 |
| `last_region` | 复用最近一次成功选区 |

窗口视频录制窗口自身内容（含标题栏和控制栏），其他应用遮挡它时不会进入成片。
支持 1–1800 整秒、无音频或系统声音，不支持窗口麦克风和窗口嵌套；窗口捕获
不可用时明确失败，不改录桌面。完整限制见[API参考](AGENT-API-REFERENCE.zh-CN.md#15-窗口捕获语义字段)。

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

需要稍后自动开始时，可让 agent 创建一次性或有界每日/每周计划。先在本地托盘安全控制中
启用无人值守，再为该计划重新选择固定区域并批准 Lease。agent 可以查询设置状态，
但不能通过 HTTP 代替本地授权；请求字段和限制见 `AGENT-API-REFERENCE.zh-CN.md`。

如果希望到点时再亲自决定是否开始，可让 agent 创建一次性 `required` 计划：
本地选区并批准创建后，到点仍会出现第二个本地执行确认。创建计划和执行结果
分别查询 `/plan-setups/{id}` 与 `/plans/{plan_id}/status`；已排期不等于已录制。

`countdown_seconds` 对 raw API 和 quick API 都可选，省略时为 `3`，只接受 `0..10`
的整数。设为 `0` 可关闭可见倒计时，但仍保留本地确认、准备、预检和可信首帧门槛。
该值会在本地确认摘要及录制响应/状态的 `config` 中展示。

## 保存选区与未来窗口

常用同一范围时，可让 agent “保存一个录制选区”，本地选择后保存命名配置。
以后的普通录制复用精确配置版本，仍须逐次本地确认。当前复用仅无音频、1-600 秒；
显示器环境变化会拒绝，不自动移动或缩放区域。

要录制稍后打开的窗口，可让 agent 请求绑定精确本地程序、输出目录和可选固定
系统声音设备的单次授权。你需在本地启用无人值守并批准范围，随后由 agent
打开程序、识别窗口并提交一次运行。授权最多一小时，录制最多半小时；
Recorder 不负责打开视频平台或识别开播。系统声音包含该输出设备的全部声音。

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
- 普通录制必须由本地用户逐次确认；一次性及每日/每周无人值守计划必须先完成本地固定区域选择和有界 Lease 批准。
- 一次性 `required` 计划不使用 Lease，创建时和到点执行时分别要求本地批准。
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


## Agent 调用空闲退出

用户说“退出 Recorder”时，agent 自行执行：

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe shutdown --json --data-dir "<ensure-running 返回的 data_dir>"
```

先检查 `capabilities.host_shutdown.supported`。本构建提供鉴权的 `POST /api/v1/host/shutdown`，只允许空闲退出，无 force 参数。CLI 不启动应用、不接受 PID；按 ready/capabilities、当前用户、应用路径、进程启动身份及实例 nonce 绑定原宿主，携带原 key，默认最多等待 15000ms（`--timeout-ms` 范围100–120000）确认原进程结束。只有 `exited` 表示已证实退出；`not_running` 表示没有 ready 且无宿主实例持有。`busy/unsupported/timeout/unknown` 均不表示成功退出；HTTP 202 的 `accepted` 也不代表进程已结束。

确认/选区/授权交互、准备/倒计时/录制/收尾、删除及未退休或状态未知的实际资源返回409 `HOST_BUSY`，稳定原因在 `error.details.reason_code`，busy 后服务仍可用。退出不强制取消任务，不撤销已保存计划/授权，也不授予重启或重放许可。需要停止录制时，必须有相应授权，再使用原录制停止 API 并等待资源收敛；不得把“退出应用”猜成强停录制、强杀进程或模拟托盘点击。旧服务返回 unsupported，不将每次手动托盘操作当成产品能力。已证实退出后，另行获准的重启/切换构建使用原 `ensure-running --json --app "<目标构建>" --data-dir "<同一目录>"`。

托盘 App 宿主支持此退出操作；未接入的宿主（含 Headless）公布 supported=false，CLI 如实返回 unsupported，不降级为未鉴权事件、强杀或 UI 自动化。


## 保存区域复用

可让Agent使用保存区域，搭配本次时长和系统声音或麦克风；Agent通过quick `target.type=profile_region`及精确Profile ref调用，不修改原配置。每次仍需本地确认，省略声音即无声，倒计时默认3秒。可使用已有默认设备解析，不要求每次人工报设备名称；不能唯一确定设备时才需选择。旧raw `profile_ref`仍复用完整无声配置。参见[区域复用请求](AGENT-API-REFERENCE.zh-CN.md#仅复用保存区域普通-quick)。
