# Agent Recorder：AI Agent 操作指令

本文档面向本地 AI agent。AI agent 应直接启动应用并调用 Agent Recorder 原始 HTTP API 完成录屏流程。

## 产品定位

Agent Recorder 是一款 **AI agent 原生录屏能力层**：

```text
人类用户 -> 自然语言指令 -> 本地 AI agent -> Agent Recorder 原始 API -> 本地选区/确认 UI -> MP4 输出
```

你的职责是把人类用户的自然语言意图转换为 API 调用，并在必要时引导用户完成本地 UI 选区和确认。

## 你必须遵守的原则

- 由你完成应用启动、API 调用、状态轮询和结果汇报。
- 使用 Agent Recorder 原始 HTTP API 编排录制流程。
- 不要在用户确认前声称录制已经开始。
- 不要尝试调用 HTTP 自批准接口；`POST /confirmations/{id}/approve` 被禁止。
- 不要静默录制敏感或隐私区域。
- 每次录制前都要说明即将录制的对象和时长，并等待本地用户确认。

## 启动与就绪检查

1. 定位发布包根目录，例如：

```text
<package-root>\
```

2. 启动应用：

```text
<package-root>\AgentRecorder.App\AgentRecorder.App.exe
```

建议以进程环境变量指定数据目录：

```text
AGENT_RECORDER_DATA_DIR=<package-root>\.local-data
```

这样 API key、审计日志和录制文件都保存在发布包本地目录下。

3. 轮询能力接口，直到服务就绪：

```http
GET http://127.0.0.1:37891/api/v1/capabilities
```

该接口不需要 API key。

4. 读取 API key：

```text
<package-root>\.local-data\config\api-key.txt
```

如果文件暂未出现，可以先请求一次受保护接口触发生成：

```http
GET http://127.0.0.1:37891/api/v1/recordings
```

收到 401 是正常现象，然后等待 `api-key.txt` 出现。后续受保护接口都带上：

```http
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <your-agent-name>
```

## 场景 1：用户说“帮我录制当前对话窗口 5 分钟”

推荐用选区录制，因为“当前对话窗口”对 AI agent 来说可能不等同于稳定窗口句柄。

1. 回复用户：

```text
好的，我会请求 Agent Recorder 进行选区录制。请在弹出的界面中框选当前对话窗口区域，随后确认录制。
```

2. 请求用户选区：

```http
POST /api/v1/region-selections
Content-Type: application/json

{
  "purpose": "recording",
  "timeout_seconds": 300
}
```

3. 如果返回：

```json
{
  "status": "selected",
  "display_id": "display_1",
  "coordinate_space": "virtual_screen",
  "bounds": { "x": 100, "y": 100, "width": 1200, "height": 800 }
}
```

用这些坐标创建录制：

```http
POST /api/v1/recordings
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>
X-Agent-Name: <your-agent-name>

{
  "source": {
    "type": "region",
    "display_id": "display_1",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 100, "y": 100, "width": 1200, "height": 800 }
  },
  "audio": {
    "microphone": { "enabled": false }
  },
  "video": {
    "fps": 15,
    "quality": "medium"
  },
  "output": {
    "directory": "default",
    "filename_template": "recording-{datetime}"
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

4. 如果返回 `requires_user_confirmation`，告诉用户：

```text
选区已确认。请在本地确认窗口或系统托盘中批准录制。录制会在你确认后开始。
```

5. 轮询：

```http
GET /api/v1/confirmations/{confirmation_id}
```

直到 `status=approved` 并取得 `recording_id`。如果状态是 `rejected` 或 `expired`，向用户说明录制没有开始。

6. 轮询：

```http
GET /api/v1/recordings/{recording_id}
```

直到 `status=completed`，然后向用户报告：

```text
录制已完成。
- 视频路径：<output.path>
- 时长：<output.duration_seconds> 秒
- 分辨率：<output.width>x<output.height>
- 文件大小：<output.bytes_written>
```

## 场景 2：用户说“选区录屏 3 分钟”

同场景 1，但 `stop_condition.seconds` 设置为 `180`。

你可以直接说：

```text
好的，请在弹出的选区界面中框选要录制的区域。录制需要你在本地确认后才会开始。
```

## 场景 3：用户说“开始外层录制，然后在里面再录制一个窗口 1 分钟”

这是嵌套录制。外层记录整个过程，内层记录再次选择的区域。

1. 获取显示器：

```http
GET /api/v1/displays
X-Agent-Recorder-Key: <api-key>
```

选择主显示器或第一个可用显示器。

2. 创建外层录制：

```json
{
  "source": {
    "type": "display",
    "display_id": "display_1"
  },
  "audio": {
    "microphone": { "enabled": false }
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
    "session_id": "nested-<timestamp>"
  },
  "safety": {
    "require_user_confirmation": true
  }
}
```

3. 等待用户确认外层录制，并取得外层 `recording_id`。

4. 当用户提出内层录制需求时，请求选区并创建内层录制：

```json
{
  "source": {
    "type": "region",
    "display_id": "<region display_id>",
    "coordinate_space": "virtual_screen",
    "bounds": { "x": 100, "y": 100, "width": 800, "height": 600 }
  },
  "audio": {
    "microphone": { "enabled": false }
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
    "session_id": "nested-<same timestamp>"
  },
  "safety": {
    "require_user_confirmation": true
  }
}
```

5. 等待内层和外层都完成，然后报告两段视频路径，并说明外层视频记录了内层录制的发起过程。

## 失败与拒绝处理

- `selection_cancelled`：用户取消选区，告诉用户录制未开始。
- `selection_timeout` 或 `SELECTION_TIMEOUT`：用户未及时选区，建议重试。
- `rejected`：用户拒绝确认，告诉用户录制未开始。
- `expired`：确认超时，建议重试。
- `SOURCE_NOT_FOUND`：重新调用 `/displays` 或 `/windows` 获取来源。
- `METHOD_NOT_ALLOWED`：不要尝试 HTTP 自批准，提醒用户必须本地确认。

## API 手册

更完整的端点、请求体和响应格式见：

```text
AGENT-API-REFERENCE.zh-CN.md
```
