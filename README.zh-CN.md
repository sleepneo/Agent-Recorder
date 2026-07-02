# Agent Recorder 中文说明

Agent Recorder 是一款面向 AI agent 的本地 Windows 录屏服务。它提供一组可由本地 AI agent 直接调用的录屏 API。

核心流程：

```text
人类用户 -> 自然语言指令 -> 本地 AI agent -> Agent Recorder 原始 API -> 本地选区/确认 UI -> MP4 输出
```

AI agent 应阅读 `AGENT-INSTRUCTIONS.zh-CN.md` 和 `AGENT-API-REFERENCE.zh-CN.md`，然后通过原始 HTTP API 完成录制。

## 产品价值

- 人类用户只需要说“帮我录制当前窗口 5 分钟”或“选区录屏 30 秒”。
- AI agent 负责启动应用、读取 API key、调用 API、轮询状态并报告结果。
- Agent Recorder 负责本地选区、确认、录制、输出和审计。
- 每次录制都需要本地用户确认，AI agent 不能静默录屏。
- 支持嵌套录制，外层视频可以记录 AI agent 发起内层录制的过程。

## 人类用户如何使用

1. 解压 Windows portable zip。
2. 把以下文档交给本地 AI agent：
   - `AGENT-INSTRUCTIONS.zh-CN.md`
   - `AGENT-API-REFERENCE.zh-CN.md`
3. 对 AI agent 发出自然语言指令，例如：

```text
帮我选区录屏 30 秒。
```

4. 在弹出的选区 UI 中框选区域。
5. 在本地确认窗口或系统托盘中批准录制。
6. 等待 AI agent 报告 MP4 输出路径。

## AI agent 如何接入

AI agent 应直接使用原始 API：

- `GET /api/v1/capabilities`
- `POST /api/v1/region-selections`
- `POST /api/v1/recordings`
- `GET /api/v1/confirmations/{id}`
- `GET /api/v1/recordings/{id}`

启动应用时建议设置：

```text
AGENT_RECORDER_DATA_DIR=<package-root>\.local-data
```

这样 API key、视频和审计日志都保存在发布包本地目录下。

## 当前能力

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 显示器录制 | 已实现 | 录制整个显示器 |
| 窗口录制 | 已实现 | 按窗口信息定位窗口 |
| 选区录制 | 已实现 | 用户在屏幕上拖拽选择区域并确认 |
| 嵌套录制 | 已实现 | 外层录制过程中可以启动内层录制 |
| 本地确认 | 已实现 | 每次录制均需人类用户确认 |
| 阻止 HTTP 自批准 | 已实现 | `/confirmations/{id}/approve` 返回 405 |
| API Key | 已实现 | 状态变更接口需要 `X-Agent-Recorder-Key` |
| 审计日志 | 已实现 | 录制生命周期写入本地 JSONL 日志 |
| 音频录制 | 未实现 | 当前版本暂不包含音频 |
| 代码签名 | 未实现 | 便携包可能触发 SmartScreen 提示 |

## 项目结构

```text
AgentRecorder.sln
src/
  AgentRecorder.App            WinForms 托盘程序与本地确认 UI
  AgentRecorder.Api            本地 HTTP API
  AgentRecorder.Core           录制状态机与核心契约
  AgentRecorder.Capture        FFmpeg 捕获后端
  AgentRecorder.Windows        Win32 显示器/窗口枚举
  AgentRecorder.Security       安全策略与自批准拦截
  AgentRecorder.Logging        审计日志
tests/
  AgentRecorder.Tests
tools/
  ffmpeg/bin                   随包分发的 FFmpeg/ffprobe
```

## 构建与测试

源码开发者可以运行：

```powershell
dotnet build AgentRecorder.sln --configuration Release --no-restore
dotnet test AgentRecorder.sln --configuration Release --no-restore
```

这些是开发验证命令，不是普通用户使用路径。

## 安全边界

- API 只监听 `127.0.0.1`。
- 录制前必须由本地用户确认。
- HTTP 自批准接口被明确阻止。
- API key 保存在本地 `.local-data\config\api-key.txt`。
- 录制生命周期写入 `.local-data\logs\audit.jsonl`。

## 已知限制

- 当前仅支持 Windows。
- 当前版本暂不录制音频。
- FFmpeg `gdigrab` 对部分 GPU 加速窗口可能不稳定。
- 便携包尚未做代码签名。

## 许可证

Agent Recorder 采用 MIT License。便携包内包含的 FFmpeg 二进制仍受 FFmpeg 自身许可约束，详见 `LICENSE-NOTICE.md`。
