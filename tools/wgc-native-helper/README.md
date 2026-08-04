# wgc-native-helper

`wgc-native-helper.exe` 是一个隔离的原生 helper 进程，使用 C++/WinRT、Windows Graphics Capture（WGC）和 Media Foundation 软件 H.264 编码，对单个显示器进行短时连续录制，输出标准 MP4。

本 helper 不直接对外提供 HTTP API，而是由主进程通过命令行启动并通过 stdout 上的 IPC v2 事件流监督生命周期。

## 构建要求

- Windows 10 版本 1903（build 18362）或更高版本（WGC 必需）。
- Visual Studio Build Tools 2022 或更高版本，安装 **C++ 桌面开发**工作负荷。
- Windows SDK `10.0.26100.0` 或兼容版本（含 C++/WinRT、`cppwinrt.exe`）。
- 仅依赖 Windows 系统库与 SDK，不引入 vcpkg、NuGet 或网络下载依赖。
- 使用 `/W4`，项目源码不产生 warning。
- 静态链接 MSVC runtime（`MultiThreaded` / `MultiThreadedDebug`）。

## 构建

在项目根目录打开 PowerShell：

```powershell
.\tools\wgc-native-helper\build-native.ps1
```

脚本会：

1. 通过 `vswhere.exe` 动态发现 Visual Studio Build Tools 与 MSBuild。
2. 构建 `wgc-native-helper.vcxproj`（Release|x64）。
3. 构建 `wgc-native-helper-tests.vcxproj` 并运行原生单元测试。
4. 将 Release 可执行文件复制到 `tools\wgc-native-helper\bin\wgc-native-helper.exe`（`WgcHelperExePathResolver` 默认查找位置）。

跳过运行测试（仍构建测试工程）：

```powershell
.\tools\wgc-native-helper\build-native.ps1 -SkipRunTests
```

`-SkipTests` 保留为兼容别名，行为与 `-SkipRunTests` 相同。

## CLI 契约

### 连续 display 录制模式

```text
wgc-native-helper.exe
  --capture-continuous-display
  --display-bounds <x,y,width,height>
  --recording-id <safe-id>
  --output <absolute-mp4-path>
  --duration-ms <1000..10000>
  --fps <1..60>
  --begin-signal <absolute-path>
  --begin-token <unguessable-token>
  --begin-timeout-ms <100..300000>
  --stop-signal <absolute-path>
  --i-understand-this-captures-screen
```

参数说明：

| 参数 | 说明 |
| --- | --- |
| `--capture-continuous-display` | 启用连续 display 录制模式。 |
| `--display-bounds` | 目标显示器的完整矩形（虚拟屏幕坐标），用于精确匹配 `HMONITOR`。 |
| `--recording-id` | 1–64 个字符，仅允许字母、数字、`-`、`_`、`.`。 |
| `--output` | 绝对 `.mp4` 输出路径，必须位于 `.local-data\wgc-tests\` 或系统临时目录。 |
| `--duration-ms` | 录制时长，1000–10000 毫秒。 |
| `--fps` | 目标帧率，1–60。 |
| `--begin-signal` | 授权信号文件路径，必须位于 `.local-data\wgc-control\` 或系统临时目录。 |
| `--begin-token` | 授权令牌，信号文件内容必须与之完全匹配。 |
| `--begin-timeout-ms` | 等待 begin 信号的超时时间。 |
| `--stop-signal` | 停止信号文件路径，创建该文件可触发 graceful finalize。 |
| `--i-understand-this-captures-screen` | 显式 consent flag；缺少时在触碰屏幕内容前失败。 |

### 辅助模式

```powershell
# 帮助信息
wgc-native-helper.exe --help

# 版本号
wgc-native-helper.exe --version

# 能力探测（不触碰屏幕内容，不创建 capture session）
wgc-native-helper.exe --probe
```

`--probe` 检查：

- OS 版本是否满足 WGC 要求（Windows 10 1903+）。
- 当前 DPI awareness 上下文（必须为 `per_monitor_v2`）。
- 显示器数量与每个显示器的物理像素边界（`x,y,width,height`）及 primary 标记。
- D3D11 设备（含 WARP 回退）能否初始化。
- 能否通过 `MFTEnumEx` 创建 H.264 软件编码器。

输出示例：

```text
RESULT: OK
DpiAwareness: per_monitor_v2
MonitorCount: 2
Monitor[0]: x=0 y=0 width=3840 height=2160 primary=true
Monitor[1]: x=3840 y=0 width=1920 height=1080 primary=false
WgcSupported: true
D3d11Initialized: true
EncoderCreated: true
```

## Consent Invariant

本 helper 的最高优先级约束：**在收到有效的 begin 授权之前，不得调用 `GraphicsCaptureSession.StartCapture`，也不得将任何屏幕帧数据传入编码器。**

执行顺序：

1. 解析并校验 CLI 参数、路径安全策略。
2. 初始化 COM、WinRT、D3D11、Media Foundation 和编码器。
3. 持续轮询；每次轮询先检查 `--stop-signal`，再检查 `--begin-signal`。
4. 若 stop 在有效 begin 之前已存在，立即输出 `FAIL`（cancelled-before-begin），`StartCapture` 调用次数为 0。
5. 等待 `--begin-signal` 文件存在且其内容等于 `--begin-token`。
6. begin 通过后立即调用 `StartCapture` 并输出 `RESULT: STARTED`。
7. 以 begin 通过时刻为唯一单调起点，在 `--duration-ms` 内持续捕获，或直到检测到 `--stop-signal` 文件。
8. 安全 finalize，原子发布最终 MP4，输出 `OK` / `STOPPED` / `FAIL`。

begin 授权一次性消费：token 不匹配、超时、取消或 stop-before-begin 都会输出 `FAIL`，`FramesCaptured=0`，不会生成最终 MP4。

## IPC v2 事件流

stdout 仅输出 blank-line-delimited 事件块，诊断信息写 stderr。每个事件块后立即 flush。

### STARTED

```text
RESULT: STARTED
Stage: SessionStarted
RecordingId: <id>
Output: <path>
Container: mp4
Codec: h264
Fps: <fps>
Width: <width>
Height: <height>
CaptureMethod: WGC_D3D11_FRAME_STREAM
```

### PROGRESS

```text
RESULT: PROGRESS
Stage: Capturing
FramesCaptured: <n>
FramesDropped: <n>
ElapsedMs: <ms>
BytesWritten: <bytes>
```

### OK

```text
RESULT: OK
Stage: Complete
FramesCaptured: <n>
FramesDropped: <n>
DurationMs: <ms>
FileSize: <bytes> bytes
Width: <width>
Height: <height>
```

### STOPPED

```text
RESULT: STOPPED
StopReason: user_requested
FramesCaptured: <n>
FramesDropped: <n>
DurationMs: <ms>
FileSize: <bytes> bytes
Width: <width>
Height: <height>
```

### FAIL

```text
RESULT: FAIL
ErrorCode: <code>
Reason: <text>
HRESULT: <optional>
PartialOutputPath: <optional>
FramesCaptured: <n>
BytesWritten: <bytes>
```

## 架构要点

- **显示器匹配**：helper 通过嵌入的 Per-Monitor V2 manifest 将进程设置为物理像素坐标空间，再由 `wmain` 入口调用 `SetProcessDpiAwarenessContext`（manifest 已固定时返回 `ERROR_ACCESS_DENIED`，随后验证当前上下文）做二次确认；之后根据 `--display-bounds` 枚举 `HMONITOR` 并做完整矩形精确匹配。找不到或多匹配时失败。`--display-bounds` 始终表示虚拟桌面的物理像素，与 Agent Recorder API 的 `/api/v1/displays[].bounds` 一致。
- **D3D/WGC**：使用 BGRA-capable D3D11 设备，通过 `IGraphicsCaptureItemInterop::CreateForMonitor` 创建 capture item，`Direct3D11CaptureFramePool::CreateFreeThreaded` 接收帧。保留系统默认 WGC 隐私边框，不关闭 `IsBorderRequired`。
- **帧背压**：有界帧队列（最大 3 帧），队列满时按策略丢旧帧或拒绝新帧并累计 `FramesDropped`。`FrameArrived` 回调只做 `TryGetNextFrame`、最小校验和有界入队；GPU->CPU 拷贝与编码在 worker 线程执行。
- **编码**：Media Foundation Sink Writer，输入 `RGB32`（top-down，BGRA 内存布局直接映射），输出 `H.264`/`MFVideoFormat_H264`，软件编码优先（`MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS=FALSE`）。
- **尺寸归一化**：编码器要求宽高为偶数且 ≥32；实际输出尺寸在 `STARTED`/`OK`/`STOPPED` 中报告。每帧检查 `ContentSize`，尺寸变化时安全失败关闭。
- **时间戳**：保留 WGC `SystemRelativeTime` 的帧间关系，但在写入 MP4 前归一化到会话零点：首帧样本时间为 0，后续为 `current - firstAccepted`，丢弃非单调时间戳并计入 `FramesDropped`。
- **原子发布**：先写入同目录 `<pid>.partial.mp4`，成功后通过 `MoveFileExW`（不带 `MOVEFILE_REPLACE_EXISTING`）原子重命名为最终文件。
- **资源清理**：进程退出、取消或失败时撤销 frame arrived 事件、关闭 capture session/frame pool、等待在途回调和 worker 结束、释放 COM/Media Foundation。`MFStartup`/`MFShutdown` 与 `CoInitializeEx`/`CoUninitialize` 在所有路径下严格配对。

## 安全与路径策略

- 拒绝相对路径输入；仅接受 `C:\...` 或 `\\...` 形式的绝对路径。
- 使用 `GetFullPathNameW` canonical 化 `.`/`..` 并统一分隔符。
- 通过 `GetFinalPathNameByHandle` 解析已存在路径组件的重解析点（symlink/junction），对最终文件不存在的输出路径解析最深现存祖先，防止路径逃逸。
- `IsPathContained` 统一使用小写路径并检查分隔符边界，防止 `C:\foo` 匹配 `C:\foobar`。
- 拒绝控制字符、通配符、设备路径（`\\.\`）和非法文件名字符。
- 输出路径必须是绝对 `.mp4`，父目录存在且可写，最终文件与 partial 占位文件均不存在。
- 控制信号路径必须位于 `.local-data\wgc-control\` 或系统临时目录；begin 与 stop 路径必须不同。
- partial 文件名包含进程 ID 并以 `CREATE_NEW` 创建，防止并发碰撞；最终文件发布不使用 `MOVEFILE_REPLACE_EXISTING`。
- `recording-id` 有白名单与长度限制。

## 已知限制

- 本轮仅实现 **单个 display** 连续录制，不做 window、region、硬件编码、麦克风或系统声音。
- 显示器尺寸变化时本轮选择失败关闭，不继续写出结构损坏的 MP4。
- Windows 自带的 WGC 黄色边框是系统隐私提示，本 helper 不尝试绕过或隐藏。
- 2026-08-03 已完成受控 selector 产品路径的主屏 `3840x2160`、30 FPS、10 秒真实录制，产出 300 帧、10.000 秒且可由 FFprobe 解析的 H.264 MP4。C# 托管会话、`ICaptureBackend`、非捕获可用性探测、短期缓存和 FFmpeg 回退均已接线；self-contained portable 包会在 `AgentRecorder.WgcHelper\wgc-native-helper.exe` 携带唯一生产 helper。公共 API 仍拒绝 WGC continuous 录制，默认 FFmpeg 后端未改变；下一步是完成 10/10 真实录制稳定性验收，而不是继续扩展公共能力。

## 测试

原生单元测试覆盖 CLI 解析、路径策略（含重解析点与控制字符）、begin gate、event writer、帧时间线、像素布局、尺寸规划，以及使用仓库 ffprobe 对合成 MP4 进行的编码验证：

```powershell
.\tools\wgc-native-helper\bin\x64\Release\wgc-native-helper-tests.exe
```

C# 侧覆盖 `WgcContinuousEventStreamParser`、托管异步会话、`WgcContinuousCaptureBackend`、staging 原子发布、真实父子进程树正反对照，以及 `WgcContinuous*`、`WgcEvidence*`、`WgcErrorTaxonomyTests`、`WgcContinuousPublicBoundaryTests` 等契约测试。

## 真实桌面验收边界

`--capture-continuous-display` 涉及真实屏幕内容采集，**不得在没有人类当场确认的情况下自动执行**。真实 10 秒桌面验收由项目负责人确认 display bounds、输出路径和 begin token 后执行。
