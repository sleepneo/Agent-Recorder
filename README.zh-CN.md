# Agent Recorder 中文说明

> 本文适用于 v0.1.22。新录制仅交付视频；使用旧包时以实际运行实例的 capabilities 为准。

Agent Recorder 是一款面向 AI agent 的本地 Windows 录屏能力层。人类用户用自然语言说明要录制什么，本地 AI agent 调用 localhost API，Agent Recorder 负责本地选区、确认、录制、输出和审计。

核心流程：

```text
人类用户 -> 自然语言指令 -> 本地 AI agent -> quick API -> 本地选区/确认 UI -> MP4 输出
```

新视频请求同时省略 `output.filename` 和 `output.filename_template` 时，自动文件名为 `recording-{datetime}-{id}.mp4`，使用本次已有录制 ID 区分自动名称，不承诺永久全局唯一。显式名称、模板及冲突策略保持不变；本地更改保存目录不会重新生成时间或录制 ID，已有文件冲突仍按选定策略处理。

## 产品价值

- 人类用户只需要说“帮我录制当前窗口 5 分钟”或“选区录屏 30 秒”。
- AI agent 负责启动应用、读取 API key、调用 API、轮询状态并报告结果。
- 常见录屏意图优先使用 `POST /api/v1/recordings/quick`，减少 agent 往返。
- Agent Recorder 保留本地安全边界：普通录制需要用户逐次确认；可选的一次性及每日/每周计划都必须先由用户在本地重新选择固定区域并批准有界 Lease，AI agent 不能自行授权或扩大范围。
- 可选未来窗口一次性录制仅在能力开放时可用：agent 只能提交待审批范围，用户须在本地明确批准；授权绑定本机可执行文件、一个运行窗口、音频端点、输出目标和时长，并可在本地安全中心撤销。这不提供直播识别或自动启动播放器。
- 支持嵌套录制，外层视频可以记录 AI agent 发起内层录制的过程。
- 录制控件采用角色感知的捕获可见性：默认 FFmpeg 路径下普通录制画面保持干净，嵌套外层可记录内层录制控件和操作过程；跨 Windows/DWM 环境的捕获排除属于 best-effort 行为。

## 人类用户如何使用

v0.1.22 是 Windows x64 自包含便携预发布版。1–30 秒普通录制可由包内 CLI 一次完成启动、提交 quick 请求和等待成片；选区及录制批准仍由用户在本地操作。长录制和高级场景继续使用现有 HTTP API。常用指令见[快速开始](QUICKSTART.zh-CN.md)。

所有新视频仅交付视频，普通 raw/quick 请求省略 artifacts 即可，空对象或 bundle=false 兼容，显式 true 在录制准备前拒绝。声音选项与截图序列不变。已有 Bundle 的历史读取、五附件核验和删除保护继续兼容，不改写旧数据。成功视频仍需主文件锚点与真实资源退休；未知保持保护。运行能力以 /capabilities 为准。

升级时请先备份数据目录。本版继续使用 SQLite V51；从更早的数据库版本迁移后，不要用不支持 V51 的程序打开。

1. 解压 Windows 便携 ZIP，将解压目录作为本地 AI agent 的工作目录，或把完整路径交给它。
2. 按[快速开始](QUICKSTART.zh-CN.md#如何使用)的首次使用指令让 agent 接入。之后只需说要录什么，例如：

```text
帮我选区录屏 30 秒。
```

3. 在弹出的选区 UI 中框选区域，再核对并批准本地录制确认。完成后由 agent 返回 MP4。

### 声音录制规则

**默认只录画面，不录声音。** 只说“录制整个 PotPlayer 窗口 20 秒”不会自动开启音频；“整个窗口”指画面范围。需要声音时，请在自然语言指令中明确说明。

| 模式 | 录制内容 | 当前支持范围 |
| --- | --- | --- |
| 无声音（默认） | 仅画面，不创建音轨 | 普通窗口、选区、显示器视频 |
| 系统声音 | 所选扬声器或耳机输出的全部声音 | 普通窗口、选区、显示器视频 |
| 麦克风 | 所选麦克风输入 | 普通选区、显示器视频；窗口内容录制不支持 |
| 系统声音＋麦克风 | 两者混合 | 目前不支持同时开启 |

例如，可以对 AI agent 说：

```text
录制 PotPlayer 整个窗口 20 秒，开启系统声音，不录麦克风。
选区录屏 15 秒，使用 AirPods 麦克风，不录系统声音。
录制主屏 15 秒，不录任何声音。
```

系统声音不是指定窗口的独占音轨：即使画面只录 PotPlayer，同一输出设备上其他应用的声音也可能录入。未指定设备时，系统声音使用 Windows 当前多媒体默认输出；麦克风选择可唯一确定的默认或单一活动输入，无法唯一确定时由 agent 查询设备并消歧。批准后的设备固定于本次录制，切换 Windows 默认设备不会自动改录另一设备。

录制前请在本地确认窗核对声音模式及设备。要求录声音但设备或对应捕获能力不可用时，agent 应明确告知，不能把静音录制当作请求已完成，也不能擅自开启麦克风。旧 raw `profile_ref` 执行完整无声配置；普通 quick `profile_region` 可只复用区域，并选择本次时长、系统声音或麦克风，不修改原配置。固定区域无人值守计划及截图序列仍无声；实时支持范围以服务能力响应为准。

### 辅助查看与停止

托盘中的「观察控制中心」可选用于查看当前录制、历史结果、打开保存目录及停止指定录制；它不是录屏主入口，也不是 agent 的诊断依赖。关闭只关闭该窗口。录制与诊断信息由 agent 直接读 API，用户仍通过自然语言提出录屏需求。

需要查找以前的录制时，可按来源、时间和普通录制状态筛选分页历史，并复制路径或打开保存目录。停止请求、历史完成事实与当前文件是否存在分别显示，不将它们混为成片成功。

观察控制中心默认允许普通 Windows 截图；如果窗口处于录制范围，可能进入录制画面，可关闭窗口或移出选区。其他录制控件和确认/授权窗口的捕获策略不变。

## AI agent 如何接入

推荐启动入口：

```text
AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json
```

推荐录制入口：

```http
POST /api/v1/recordings/quick?wait_for=recording&wait_ms=25000
```

`wait.goal_reached=true` 表示已经出现可信首帧；若 `timed_out=true`，使用返回的
`recording_id` 继续长轮询录制状态。这个等待只观察本地批准、准备、倒计时和开始状态，
不会通过 HTTP 批准录制；省略 query 时仍保持原有的立即创建响应。

### 运行数据目录

- 安装版仅面向 Windows x64 当前用户，程序固定在 `%LOCALAPPDATA%\Programs\AgentRecorder`。安装结束不启动、不录制、不启用自启、桌面 broker 或计划。agent 从 `HKCU\Software\AgentRecorder\Installation` 读取 `InstallLocation`、`CliPath`、`AgentInstructions`，经获准的当前用户桌面入口直接调用 CLI `ensure-running --json`；登记不授予跨账号启动能力。开始菜单仍打开原托盘 App。
- 安装版 App、CLI ensure/shutdown/interactive-launch status、开始菜单默认同用 `%LOCALAPPDATA%\AgentRecorder`。显式数据目录优先于 `AGENT_RECORDER_DATA_DIR`，再到默认目录；坏标记或包/用户冲突在覆盖前明确失败。数据与程序目录不得重叠或经过重解析点。不迁移、合并 portable 数据或复制密钥。
- 升级前备份数据，由 agent 通过原匹配 CLI `shutdown --json` 申请鉴权空闲退出并确认已退出。活动/未知实例、API 端口占用、未处理的本产品自启/broker 登记阻断安装、升级和卸载，silent 也不豁免，拒绝强制关闭参数。原集成需先精确核对归属，不删除未知任务。卸载只移除清单程序和本安装入口，保留视频、DB、key、配置、审计及程序根额外文件，无清空数据选项。
- 安装失败或取消可能留下部分程序文件，不承诺升级整体事务化；只有正常 App 启动迁移 DB，不以旧二进制重开升级后的数据。未签名不代表发布者已验证，不要求关闭 SmartScreen 或杀毒。
- portable 包中通过 `AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json` 启动时，默认 data-dir 是 `<package-root>\.local-data`。
- 直接运行 `AgentRecorder.App.exe` 或 `AgentRecorder.Headless.exe` 且未设置 `AGENT_RECORDER_DATA_DIR` 时，默认 data-dir 是 `%LOCALAPPDATA%\AgentRecorder`。
- AI agent 应优先使用 `ensure-running` 或 `/api/v1/capabilities` 返回的 `data_dir`、`ready_file`、`api_key_file`，不要硬编码 API key 或 ready 文件路径。

需要托盘、本地选区或确认 UI 时，agent 应先检查宿主是否提供获准的用户桌面命令执行入口，并经该入口调用 `ensure-running`。部分 agent 宿主可对具体命令使用工具授权执行入口；这是宿主调用能力，不是 Recorder CLI 参数，也不代表其他 agent/宿主均支持。Recorder 不会跳出沙盒或跨 Windows 账户派发；没有适当执行面时保持 fail-closed。`host.supports_region_selection_ui` 只表示宿主具备选区 UI，不是托盘可见性的证明。

`target.type` 支持：

| target.type | 说明 |
| --- | --- |
| `primary_display` | 录制主显示器 |
| `windows_display` | 按 Windows“标识”数字录制指定显示器 |
| `active_window` | 录制当前活动窗口 |
| `selected_region` | 弹出选区 UI，让用户框选区域后录制 |
| `last_region` | 复用最近一次成功选区 |

“录制 Windows 显示器 N”优先使用一次 quick 请求，例如：

```json
{
  "target": { "type": "windows_display", "windows_display_number": 2 },
  "duration_seconds": 30
}
```

这里的数字来自 Windows“设置 > 系统 > 显示 > 标识”，必须是正 JSON integer，
不是 `display_2` 的 API ID 后缀。服务端找不到可靠唯一编号时返回
`SOURCE_NOT_FOUND`，应刷新 capabilities/displays 或请用户消歧；成功后仍需本地
确认，API 不会自批准。raw API 仍使用 `/displays` 返回的 opaque `id`，原样传入
`source.display_id`。

复杂或精确控制场景可继续使用底层 API：`/displays`、`/windows`、`/region-selections`、`/recordings`、`/confirmations/{id}`、`/recordings/{id}`。使用 raw API 录制指定显示器时，应按 `windows_display_number` 查找条目，再把其 opaque `id` 原样作为 `source.display_id` 传回；不要自行拼接 `display_N`。

## 当前能力

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| quick API | 已实现 | 一次请求表达常见录制意图 |
| 有界创建等待 | 已实现 | raw/quick 创建请求可在同一 POST 中等待本地批准、准备、倒计时和可信首帧；不改变本地确认边界 |
| 显示器录制 | 已实现 | 录制整个显示器 |
| 窗口录制 | 已实现 | 默认捕获窗口自身内容，不录入遮挡应用；不可用明确失败，不回退桌面区域。1–1800 秒，可无声或开启系统声音，不支持窗口麦克风或无限时长。包含标题栏及控制栏，系统声音不是窗口独占。raw 已通过真实遮挡验收，quick 有自动化覆盖 |
| 独立窗口并行 | 已实现；三路规格已验收 | 分别提交普通 raw 无声窗口请求，各自确认、输出与启停，不传 nested/parent/session。默认预算 2，宿主启动可显式配置 1–10；未退休交付仍占预算。控件显示窗口编号和标题。三路已通过 180/90/30 秒验收，不代表十路硬件稳定；不混入其他目标、音频、quick/Profile 或无人值守。与桌面区域 OUTER/INNER 分层不同 |
| 未来窗口单次授权 | 已实现 | 本地批准精确程序、时长、输出和可选固定系统声音；批准后由 agent 启动程序、解析唯一 eligible 窗口并提交一次运行。有效期最多一小时，运行最多半小时，可撤销；不负责开播识别或播放器操作 |
| 固定区域配置复用 | 已实现 | 本地选区保存不可变 Profile，支持精确 ref 和版本管理。raw 复用完整无声配置；普通 quick 可只复用区域，选择本次 1–600 秒时长及系统声音或麦克风。每次仍须本地确认并核对显示器环境，不修改原配置，不接线 Profile 发起计划 |
| 选区录制 | 已实现 | 支持拖拽、精确坐标、尺寸预设、边缘/窗口吸附、点击窗口选区和双屏可靠置顶 |
| 嵌套录制 | 已实现 | 外层录制过程中可以启动内层录制 |
| 本地确认 | 已实现 | 普通录制逐次由人类确认；麦克风、普通无音频 FFmpeg 与延迟授权 WGC 路径在批准后显示不采集画面的可配置 0-10 秒倒计时 |
| 一次性有限无人值守 | 已实现 | 默认关闭；仅固定区域、单次、无音频、自然唤醒和交互桌面，Lease 最长 1 小时、Run 最长 10 分钟；必须先完成本地选区与 Lease 批准 |
| 每日/每周有限无人值守 | 已实现 | 默认关闭；固定区域、无音频、自然唤醒；日期范围、次数、总时长和 Lease 有效期均受约束，每次到点重新验证环境，不补录错过的窗口 |
| 一次性逐次确认计划 | 已实现 | 不使用 Lease；本地选区并批准创建计划后，到点还需在第二个本地窗口明确批准本次录制，拒绝、过期或无法安全显示时不启动 |
| 录制前检查 | 已实现 | 确认前和启动前检查目录、空间、编码器、区域及目标可用性 |
| 录制中提示 | 已实现 | 录制区域显示克制呼吸的红色边框和计时标签，支持 outer/inner；系统关闭动画时自动降级为静态显示 |
| 本地停止控制 | 已实现 | 每条录制有独立圆角胶囊停止控件；全局热键 `Ctrl+Shift+F10` 停止全部；托盘菜单动态显示停止入口 |
| 章节标记 | 新录制停用 | 旧历史标记保留，新录制不接收标记或注册章节热键 |
| 自有 UI 隔离 | 已实现 | 默认 FFmpeg 路径已通过真实桌面验收；普通控件尽力排除，安全的 inner 控件可由 outer 记录 |
| 双语与高 DPI | 已实现 | 本地选区、确认、REC、停止控件支持中文/英文，并适配 100%-200% DPI；选区显示四角强调和实时 `W×H @ display` 标签 |
| 阻止 HTTP 自批准 | 已实现 | `/confirmations/{id}/approve` 返回 405 |
| API Key | 已实现 | 状态变更接口需要 `X-Agent-Recorder-Key` |
| 审计日志 | 已实现 | 录制生命周期写入本地 JSONL 日志 |
| 自启管理 | 已实现 | CLI 支持当前用户级 autostart |
| Agent 交互桌面冷启动 | 已实现；依赖宿主授权执行面 | 在已验证的当前用户桌面直接启动/复用。调用前由 agent 检查宿主的用户桌面授权执行入口；Recorder 不跨账户派发。仅同用户隔离桌面的高级故障恢复可显式设置当前用户任务 |
| FFmpeg 预热 | 已实现 | 服务就绪后后台预热 FFmpeg/FFprobe |
| 性能摘要 | 已实现 | `/capabilities.perf_summary` 提供本地 cold/warm 分组 P50/P95 诊断统计 |
| 视频交付 | 当前源码 | 新视频仅交付视频位置和必要属性；旧 Bundle 继续兼容读取、核验与保护 |
| 麦克风录制 | 已实现 | 默认通过隔离的 Windows WASAPI helper 捕获麦克风，最终合流为 AAC 音轨；具备连续性诊断、运行期恢复和稳定错误码。蓝牙 Hands-Free 输入可自动配对对应渲染端点并保持 HFP 双工链路，AirPods Pro 与 Focal Bathys 已通过真实产品路径验收；不同设备和驱动仍可能存在兼容性差异。FFmpeg dshow 仅作为显式诊断回退 |
| 系统声音 | 已实现 | 隔离 WASAPI loopback、音视频分离采集、AAC 合流、确认 UI、倒计时和连续性诊断已接通；本地确认时批准的端点在本次录制中保持固定，切换 Windows 默认输出不会暗中改录其他设备，切回批准端点时采用有界同端点恢复。公开能力契约通过 `/capabilities`、`/permissions` 和 `/audio/devices.output_devices` 报告实时状态与 render endpoint |
| 通用 WGC 连续录制 | 实验性实现 | 符合条件的 1–60 秒无音频 display/window/region 路径默认关闭，具备非捕获探测、可信首帧证据、目标生命周期检查、稳定显示器身份和 GPU 区域裁剪；与上面的公开严格固定窗口路径分开，不应混用能力声明或回退语义 |
| 代码签名 | 未实现 | 便携包可能触发 SmartScreen 提示 |

计划创建状态由 `GET /api/v1/plan-setups/{id}` 查询；每次执行的持久化状态和经验证的实际媒体路径由 `GET /api/v1/plans/{plan_id}/status` 查询。`scheduled` 只表示已排期，不代表录制成功。

### 每条录制的开始前倒计时

raw API 与 quick API 都接受顶层 `countdown_seconds`。省略时默认为 `3`，只接受
`0..10` 的 JSON 整数；负数、超过 10、浮点数、字符串、布尔值、`null`、对象和数组
均返回 `400 INVALID_ARGUMENT`。设为 `0` 时仍保留确认、准备、预检和可信首帧边界，
但不显示倒计时。规范化后的值会出现在确认摘要以及录制状态的 `config` 中。

### 有界截图序列

raw 与 quick 请求可使用 `mode: "screenshot_series"`，要求 `interval_ms` 为
`1000..3600000` 的整数，并且只能提供一个有限边界：`max_count`（`1..300`）或
`max_duration_seconds`（`1..86400`）。按时长请求计划
`ceil(max_duration_seconds * 1000 / interval_ms)` 个时间点，超过 300 张会在目标、
设备或选区解析前拒绝。截图序列不支持音频；raw 请求若带 `stop_condition`，quick
请求若带 `duration_seconds` 或 `stop_condition`，均在目标/音频解析前返回
`400 INVALID_ARGUMENT`，并建议删除冲突字段或改用 `max_duration_seconds`。

每个时间点只执行一次单帧 PNG 捕获。通过本地确认和启动前检查/倒计时后，图片先写入
data-dir 下 recording-specific 的 `temp` staging，完成签名、非零大小和尺寸校验后
原子命名；最终目录包含 `frame-0001.png` 和 `series.json`。完成或有帧取消时，状态通过
`mode`、`series` 和 `output` 返回；章节标记和 MP4 bundle 对截图序列不适用。指示器显示
`截图 x/y`，不会显示误导性的 `REC`。时长边界从第一张有效 PNG 原子提交时开始计时，
该帧为 `t=0`；到达或超过 deadline 时不会再启动下一 runner，正常完成但
`planned_frame_count` 可能大于 `captured_frame_count`。所有截图序列 bounds 明确使用
`virtual_screen`，确认摘要、runner 请求和 `series.json` 使用同一坐标空间。
每个计划点先认领 scheduled start，再启动一个有限的 FFmpeg 单帧进程；gdigrab 使用
有限的低延迟输入采样率，同时保留 `-frames:v 1`，不会变成连续 worker。认领始终串行，
不会为了追赶计划点并发启动多个 frame。`series.json` 中第一张有效 PNG 提交是时间锚点
（`t=0`）；`captured_offset_ms` 是有效提交偏移，`lateness_ms` 是不包含本帧捕获/编码耗时
的非负认领迟到，`capture_duration_ms` 是从认领到有效 PNG 提交的单调时钟耗时。这些字段
用于诚实描述时序，不承诺固定桌面毫秒延迟或实时保证。

## 文档入口

- [快速开始](QUICKSTART.zh-CN.md)：人类用户与本地 AI agent 的首次使用流程。
- [Agent 指南](AGENT-INSTRUCTIONS.zh-CN.md)：启动、API 编排与安全边界。
- [API 手册](AGENT-API-REFERENCE.zh-CN.md)：字段、错误和限额；[英文版](docs/api.md)。
- [安全说明](docs/safety.md)：授权和产物证据的不变量。

## 项目结构

```text
src/
  AgentRecorder.App            WinForms 托盘程序、本地选区和确认 UI
  AgentRecorder.Api            本地 HTTP API
  AgentRecorder.Core           录制状态机与核心契约
  AgentRecorder.Capture        FFmpeg/WASAPI 捕获、合流、旧 Bundle 兼容与预热
  AgentRecorder.Windows        Win32 显示器/窗口枚举
  AgentRecorder.Security       安全策略与自批准拦截
  AgentRecorder.Logging        审计日志
tools/
  AgentRecorder.Cli            agent 启动握手和自启管理
  AgentRecorder.AudioHelper    隔离的 Windows WASAPI 音频 helper
  ffmpeg/bin                   随包分发的 FFmpeg/ffprobe
  wgc-native-helper            实验性 WGC 原生 helper 源码与构建脚本
tests/
  AgentRecorder.Tests
  AgentRecorder.AudioHelper.Tests
```

## 构建与测试

源码开发者可以运行：

```powershell
dotnet build AgentRecorder.sln --configuration Release
dotnet test tests\AgentRecorder.Tests\AgentRecorder.Tests.csproj --configuration Release --no-build --no-restore
```

## 安全边界

- API 只监听 `127.0.0.1`。
- 状态变更接口需要 API key。
- 录制前必须由本地用户确认。
- HTTP 自批准接口被明确阻止。
- API key 文件路径由 `ensure-running` 返回的 `api_key_file` 指定。
- 录制生命周期写入当前 data-dir 下的 `logs\audit.jsonl`。

## 许可证

Agent Recorder 采用 MIT License。便携包内包含的 FFmpeg 二进制仍受 FFmpeg 自身许可约束，详见 `LICENSE-NOTICE.md`。


## Agent 调用空闲退出

用户说“退出 Recorder”时，agent 自行执行：

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe shutdown --json --data-dir "<ensure-running 返回的 data_dir>"
```

先检查 `capabilities.host_shutdown.supported`。本构建提供鉴权的 `POST /api/v1/host/shutdown`，只允许空闲退出，无 force 参数。CLI 不启动应用、不接受 PID；按 ready/capabilities、当前用户、应用路径、进程启动身份及实例 nonce 绑定原宿主，携带原 key，默认最多等待 15000ms（`--timeout-ms` 范围100–120000）确认原进程结束。只有 `exited` 表示已证实退出；`not_running` 表示没有 ready 且无宿主实例持有。`busy/unsupported/timeout/unknown` 均不表示成功退出；HTTP 202 的 `accepted` 也不代表进程已结束。

确认/选区/授权交互、准备/倒计时/录制/收尾、删除及未退休或状态未知的实际资源返回409 `HOST_BUSY`，稳定原因在 `error.details.reason_code`，busy 后服务仍可用。退出不强制取消任务，不撤销已保存计划/授权，也不授予重启或重放许可。需要停止录制时，必须有相应授权，再使用原录制停止 API 并等待资源收敛；不得把“退出应用”猜成强停录制、强杀进程或模拟托盘点击。旧服务返回 unsupported，不将每次手动托盘操作当成产品能力。已证实退出后，另行获准的重启/切换构建使用原 `ensure-running --json --app "<目标构建>" --data-dir "<同一目录>"`。

托盘 App 宿主支持此退出操作；未接入的宿主（含 Headless）公布 supported=false，CLI 如实返回 unsupported，不降级为未鉴权事件、强杀或 UI 自动化。


### 保存区域复用

保存的区域可以搭配本次时长和声音使用，无需修改原配置。每次仍需本地确认，声音默认关闭、倒计时默认3秒。参见[使用说明](QUICKSTART.zh-CN.md#保存区域复用)及[API请求](AGENT-API-REFERENCE.zh-CN.md#仅复用保存区域普通-quick)。


### 独立窗口录制

独立无声窗口同时受路数和总像素率限制。配置和错误码见[启动预算](AGENT-API-REFERENCE.zh-CN.md#总像素率准入预算)；配置上限不代表本机实际性能。
