# Agent Recorder - curl 示例

## 概述

本目录包含使用 curl 调用 Agent Recorder API 的示例脚本。

## 前提条件

1. 启动 Agent Recorder 服务：
```bash
cd /path/to/AgentRecorder
.\scripts\start-server.ps1 -Configuration Release -DataDir .local-data
```

2. 服务启动后会自动生成 API Key 并保存到 `.local-data/config/api-key.txt`。

3. 设置环境变量（从文件读取，避免在命令行历史中暴露）：
```bash
# Windows (PowerShell)
$env:AGENT_RECORDER_API_KEY = Get-Content .\.local-data\config\api-key.txt

# Linux/macOS
export AGENT_RECORDER_API_KEY=$(cat .local-data/config/api-key.txt)
```

## 示例脚本

### 1. 获取服务能力

```bash
curl http://127.0.0.1:37891/api/v1/capabilities
```

### 2. 获取显示器列表

```bash
curl http://127.0.0.1:37891/api/v1/displays
```

### 3. 获取窗口列表

```bash
curl http://127.0.0.1:37891/api/v1/windows
```

### 4. 获取活动窗口

```bash
curl http://127.0.0.1:37891/api/v1/windows/active
```

### 5. 获取音频设备

```bash
curl http://127.0.0.1:37891/api/v1/audio/devices
```

### 6. 获取权限状态

```bash
curl http://127.0.0.1:37891/api/v1/permissions
```

### 7. 发起录制请求（需要 API Key）

```bash
curl -X POST http://127.0.0.1:37891/api/v1/recordings \
  -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "source": {
      "type": "display",
      "display_id": "display_0"
    },
    "audio": {
      "microphone": {
        "enabled": false
      }
    },
    "video": {
      "fps": 30,
      "quality": "medium"
    },
    "stop_condition": {
      "type": "duration",
      "seconds": 5
    }
  }'
```

### 8. 查询录制状态

```bash
curl -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
  http://127.0.0.1:37891/api/v1/recordings/rec_xyz789
```

### 9. 查询确认状态

```bash
curl -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
  http://127.0.0.1:37891/api/v1/confirmations/confirm_abc123
```

### 10. 停止录制

```bash
curl -X POST http://127.0.0.1:37891/api/v1/recordings/rec_xyz789/stop \
  -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "reason": "user_requested"
  }'
```

### 11. 列出所有录制记录

```bash
curl -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
  http://127.0.0.1:37891/api/v1/recordings
```

## 完整录制流程示例

```bash
#!/bin/bash

# 1. 获取显示器列表
echo "获取显示器列表..."
curl http://127.0.0.1:37891/api/v1/displays

# 2. 发起录制请求
echo ""
echo "发起录制请求..."
RESPONSE=$(curl -X POST http://127.0.0.1:37891/api/v1/recordings \
  -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "source": {"type": "display", "display_id": "display_0"},
    "audio": {"microphone": {"enabled": false}},
    "video": {"fps": 30, "quality": "medium"},
    "stop_condition": {"type": "duration", "seconds": 5}
  }')

echo $RESPONSE

# 3. 检查是否需要确认
CONFIRMATION_ID=$(echo $RESPONSE | jq -r '.data.confirmation_id')

if [ "$CONFIRMATION_ID" != "null" ]; then
  echo ""
  echo "等待用户确认... (confirmation_id: $CONFIRMATION_ID)"
  
  # 轮询确认状态
  while true; do
    STATUS_RESP=$(curl -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
      http://127.0.0.1:37891/api/v1/confirmations/$CONFIRMATION_ID)
    
    STATUS=$(echo $STATUS_RESP | jq -r '.data.status')
    echo "确认状态: $STATUS"
    
    if [ "$STATUS" = "approved" ]; then
      RECORDING_ID=$(echo $STATUS_RESP | jq -r '.data.recording_id')
      echo "录制已开始 (recording_id: $RECORDING_ID)"
      break
    elif [ "$STATUS" = "rejected" ]; then
      echo "用户拒绝录制"
      exit 1
    elif [ "$STATUS" = "expired" ]; then
      echo "确认超时"
      exit 1
    fi
    
    sleep 1
  done
else
  RECORDING_ID=$(echo $RESPONSE | jq -r '.data.recording_id')
fi

# 4. 等待录制完成
echo ""
echo "等待录制完成..."
while true; do
  STATUS_RESP=$(curl -H "X-Agent-Recorder-Key: $AGENT_RECORDER_API_KEY" \
    http://127.0.0.1:37891/api/v1/recordings/$RECORDING_ID)
  
  STATUS=$(echo $STATUS_RESP | jq -r '.data.status')
  echo "录制状态: $STATUS"
  
  if [ "$STATUS" = "completed" ]; then
    OUTPUT_PATH=$(echo $STATUS_RESP | jq -r '.data.output.path')
    echo "录制完成: $OUTPUT_PATH"
    break
  elif [ "$STATUS" = "failed" ]; then
    echo "录制失败"
    exit 1
  fi
  
  sleep 1
done

echo ""
echo "录制流程完成！"
```

## 错误处理示例

```bash
# 测试认证失败
curl http://127.0.0.1:37891/api/v1/recordings
# 预期: {"ok":false,"error":"UNAUTHORIZED","message":"Missing API key..."}

# 测试无效 API Key
curl -H "X-Agent-Recorder-Key: invalid-key" \
  http://127.0.0.1:37891/api/v1/recordings
# 预期: {"ok":false,"error":"FORBIDDEN","message":"Invalid API key"}
```

## 提示

1. **API Key**：所有敏感接口都需要认证，请从 token 文件读取
2. **确认超时**：用户确认对话框 30 秒超时
3. **录制状态**：录制完成后可获取输出文件路径
4. **日志**：所有操作记录在 `<数据目录>/logs/audit.jsonl`
