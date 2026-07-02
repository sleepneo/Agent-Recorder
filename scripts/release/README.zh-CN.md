# Agent Recorder Release 脚本中文说明

本目录保存发布包内可直接运行的 PowerShell 脚本。这些脚本是**安全的内部适配器**，供以下场景使用：

- **AI Agent 内部调用**：AI Agent（如 TRAE Work）调用这些脚本实现录屏功能
- **发布验证**：评审或开发者验证发布包功能
- **故障排查**：当 AI Agent 不可用时的高级备用路径

**重要**：这些脚本不是人类用户的常规操作入口。正常使用路径是：

```
人类用户 → 自然语言指令 → AI Agent → 调用这些脚本/API → 本地 UI → MP4 输出
```

人类用户不需要手动运行这些脚本，AI Agent 会帮你完成。

---

## 脚本列表

| 脚本 | 用途 |
| --- | --- |
| `start-agent-recorder.ps1` | 启动 Agent Recorder 托盘应用并等待 API 就绪 |
| `stop-agent-recorder.ps1` | 停止正在运行的 Agent Recorder |
| `smoke-capabilities.ps1` | 调用 `/api/v1/capabilities` 做快速连通性检查 |
| `probe-api-contract-noninteractive.ps1` | 非交互式 API 契约探测 |
| `record-selected-region.ps1` | 启动单层选区录制 |
| `record-nested-regions.ps1` | 启动嵌套录制演示 |
| `validate-release-script-contract.ps1` | 检查 release 脚本是否存在硬编码路径等问题 |

## AI Agent 内部调用 / 发布验证流程

以下命令供 AI Agent 内部调用，或发布验证使用。人类用户正常情况下不需要手动运行这些命令。

在发布包根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release\start-agent-recorder.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release\smoke-capabilities.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release\probe-api-contract-noninteractive.ps1
```

然后在真实交互式 Windows 桌面中测试选区录制：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release\record-selected-region.ps1 -DurationSeconds 15
```

测试嵌套录制：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release\record-nested-regions.ps1 `
  -OuterDurationSeconds 60 `
  -InnerDurationSeconds 20 `
  -InnerStartDelaySeconds 8
```

停止应用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release\stop-agent-recorder.ps1
```

## API Key

首次启动后，应用会在发布包根目录生成：

```text
.local-data\config\api-key.txt
```

所有 release 脚本会自动读取该文件，并在需要认证的请求中加入 `X-Agent-Recorder-Key` 请求头。

## 录制文件位置

默认输出位置：

```text
.local-data\Videos\
```

审计日志位置：

```text
.local-data\logs\audit.jsonl
```

## 选区录制注意事项

选区 UI 必须运行在真实交互式 Windows 桌面中。如果通过后台任务、非交互式测试进程或受限沙箱启动，可能出现 API 正常但选区窗口不可见的情况。

当窗口不可见时，优先确认：

- 当前是否在真实桌面会话中运行。
- 是否从用户可见的 PowerShell 或 AI agent 桌面会话启动。
- 是否已有旧的 Agent Recorder 进程占用端口。
- 是否被安全软件阻止弹窗或托盘交互。

## 安全边界

- AI agent 可以发起录制请求。
- 录制必须由本地用户确认。
- HTTP 自批准接口被阻止。
- API 默认只绑定 `127.0.0.1`。

这套脚本是给评审和 AI agent 使用的最小发布流程，不包含开发环境专用脚本。
