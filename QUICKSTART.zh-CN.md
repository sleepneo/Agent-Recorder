# Agent Recorder 快速开始

Agent Recorder 是一款 **AI agent 原生录屏能力层**。录制流程由本地 AI agent 通过原始 HTTP API 完成。

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
   - 启动 `AgentRecorder.App\AgentRecorder.App.exe`
   - 设置数据目录 `AGENT_RECORDER_DATA_DIR=<package-root>\.local-data`
   - 等待 `GET /api/v1/capabilities` 就绪
   - 读取 `.local-data\config\api-key.txt`
   - 调用 `/region-selections`、`/recordings`、`/confirmations/{id}`、`/recordings/{id}` 等原始 API
   - 录制完成后报告 MP4 输出路径和元数据

5. 人类用户只需要：
   - 在弹出的选区界面中框选区域
   - 在本地确认窗口或系统托盘中批准录制
   - 播放 AI agent 返回的视频文件

## 使用要点

- 人类用户通过自然语言提出录屏需求。
- AI agent 通过原始 API 编排录制流程。
- 录制必须经过本地用户确认。
- 选区录制可交互、可控。
- 嵌套录制可记录 AI agent 发起另一次录制的过程。

## 文件位置

如果 AI agent 启动应用时设置：

```text
AGENT_RECORDER_DATA_DIR=<package-root>\.local-data
```

则默认文件位置为：

- API key：`.local-data\config\api-key.txt`
- 录制文件：`.local-data\Videos\`
- 审计日志：`.local-data\logs\audit.jsonl`

## 安全边界

- API 默认只监听 `127.0.0.1`。
- 状态变更接口需要 `X-Agent-Recorder-Key`。
- AI agent 可以请求录制，但不能静默录制。
- 每次录制都必须由本地用户确认。
- HTTP 自批准接口被阻止，返回 405。

## 发布包里有什么

```text
AgentRecorder.App\                 应用主体
README.zh-CN.md                    中文说明
QUICKSTART.zh-CN.md                本文件
AGENT-INSTRUCTIONS.zh-CN.md        AI agent 操作指令
AGENT-API-REFERENCE.zh-CN.md       原始 API 快速手册
LICENSE                           Agent Recorder MIT 许可证
LICENSE-NOTICE.md                  第三方许可说明
```

真正的产品价值是 AI agent 通过清晰 API 使用本地录屏能力。
