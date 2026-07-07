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
   - 优先调用 `POST /api/v1/recordings/quick`
   - 轮询 `/confirmations/{id}` 等待本地用户确认
   - 轮询 `/recordings/{id}` 等待录制完成
   - 录制完成后报告 MP4 输出路径和元数据

5. 人类用户只需要：
   - 在弹出的选区界面中框选区域
   - 在本地确认窗口或系统托盘中批准录制
   - 播放 AI agent 返回的视频文件

## quick API 目标类型

| target.type | 说明 |
| --- | --- |
| `primary_display` | 录制主显示器 |
| `active_window` | 按当前活动窗口的可见边界录制 |
| `selected_region` | 弹出选区 UI，让用户框选区域后录制 |

请求示例：

```json
{
  "target": { "type": "selected_region", "selection_timeout_seconds": 120 },
  "duration_seconds": 30,
  "video": { "fps": 30, "quality": "medium" }
}
```

## 文件位置

如果应用启动时设置：

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
AgentRecorder.Cli\                 agent 启动握手工具
README.zh-CN.md                    中文说明
QUICKSTART.zh-CN.md                本文件
AGENT-INSTRUCTIONS.zh-CN.md        AI agent 操作指令
AGENT-API-REFERENCE.zh-CN.md       API 快速手册
LICENSE                            Agent Recorder MIT 许可证
LICENSE-NOTICE.md                  第三方许可说明
```
