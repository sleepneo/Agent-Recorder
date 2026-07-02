# Agent Recorder - Python 示例

## 概述

本目录包含使用 Python 调用 Agent Recorder API 的示例代码。

## 依赖安装

```bash
pip install requests
```

## 使用方法

### 基本用法

```bash
# 录制显示器 5 秒（默认）
python recorder_client.py

# 录制 10 秒
python recorder_client.py --duration 10

# 启用麦克风
python recorder_client.py --microphone

# 录制窗口
python recorder_client.py --window

# 设置自定义帧率和质量
python recorder_client.py --fps 60 --quality high
```

### 命令行参数

```bash
python recorder_client.py --help

usage: recorder_client.py [-h] [--duration DURATION] [--microphone]
                          [--fps {15,24,30,60}] [--quality {low,medium,high}]
                          [--window] [--base-url BASE_URL] [--api-key API_KEY]

Agent Recorder Python 客户端

optional arguments:
  -h, --help            show this help message and exit
  --duration DURATION   录制时长（秒）
  --microphone          启用麦克风
  --fps {15,24,30,60}   帧率
  --quality {low,medium,high}
                        视频质量
  --window              录制窗口而非显示器
  --base-url BASE_URL   服务地址
  --api-key API_KEY     API Key
```

### 设置 API Key

```bash
# 方式 1：环境变量
export AGENT_RECORDER_API_KEY="your-api-key"
python recorder_client.py

# 方式 2：命令行参数
python recorder_client.py --api-key "your-api-key"
```

## 客户端功能

### 初始化客户端

```python
from recorder_client import AgentRecorderClient

# 使用默认配置
client = AgentRecorderClient()

# 使用自定义配置
client = AgentRecorderClient(
    base_url="http://127.0.0.1:37891",
    api_key="your-api-key"
)
```

### 获取信息

```python
# 获取服务能力
caps = client.get_capabilities()
print(caps["data"]["app"]["version"])

# 获取显示器列表
displays = client.get_displays()
print(displays["data"]["displays"])

# 获取窗口列表
windows = client.get_windows(include_minimized=False)
print(windows["data"]["windows"])

# 获取活动窗口
active = client.get_active_window()
print(active["data"]["window"])

# 获取音频设备
audio = client.get_audio_devices()
print(audio["data"]["input_devices"])

# 获取权限状态
permissions = client.get_permissions()
print(permissions["data"])
```

### 录制操作

```python
# 发起录制请求
result = client.start_recording(
    source_type="display",
    source_id="display_0",
    duration_seconds=5,
    enable_microphone=False,
    fps=30,
    quality="medium"
)

# 检查是否需要确认
if result["data"]["status"] == "requires_user_confirmation":
    confirmation_id = result["data"]["confirmation_id"]
    
    # 轮询确认状态
    while True:
        status = client.get_confirmation_status(confirmation_id)
        if status["data"]["status"] == "approved":
            recording_id = status["data"]["recording_id"]
            break
        time.sleep(1)
else:
    recording_id = result["data"]["recording_id"]

# 获取录制状态
status = client.get_recording_status(recording_id)
print(status["data"]["status"])

# 停止录制
result = client.stop_recording(recording_id, reason="user_requested")

# 获取录制列表
recordings = client.get_recording_list()
print(recordings["data"]["recordings"])
```

## 完整示例

```python
import time
from recorder_client import AgentRecorderClient

# 初始化客户端
client = AgentRecorderClient()

# 获取显示器列表
displays = client.get_displays()
display_id = displays["data"]["displays"][0]["id"]

# 发起录制请求
result = client.start_recording(
    source_type="display",
    source_id=display_id,
    duration_seconds=5
)

# 处理确认流程
if result["data"]["status"] == "requires_user_confirmation":
    confirmation_id = result["data"]["confirmation_id"]
    print("等待用户确认...")
    
    while True:
        status = client.get_confirmation_status(confirmation_id)
        if status["data"]["status"] == "approved":
            recording_id = status["data"]["recording_id"]
            break
        time.sleep(1)
else:
    recording_id = result["data"]["recording_id"]

# 等待录制完成
print("录制中...")
while True:
    status = client.get_recording_status(recording_id)
    if status["data"]["status"] == "completed":
        print(f"录制完成: {status['data']['output']['path']}")
        break
    time.sleep(1)
```

## 错误处理

```python
from recorder_client import AgentRecorderClient

client = AgentRecorderClient()

try:
    result = client.start_recording(...)
except Exception as e:
    print(f"错误: {e}")
```

## 注意事项

1. **服务启动**：使用前确保 Agent Recorder 服务已启动
2. **API Key**：
   - 服务启动时会自动生成 API Key 并保存到 `.local-data/config/api-key.txt`
   - Python 客户端会自动从环境变量或 token 文件读取 API Key
   - 也可通过命令行参数 `--api-key` 显式指定
3. **用户确认**：录制需要用户手动确认，确认对话框 30 秒超时
4. **网络访问**：服务仅监听 localhost，无法远程访问

## 快速开始

```bash
# 1. 启动服务（会自动生成 API Key）
.\scripts\start-server.ps1 -Configuration Release -DataDir .local-data

# 2. Python 客户端会自动读取 token 文件，直接运行即可
python recorder_client.py

# 如需手动设置环境变量（可选）：
# Windows (PowerShell)
$env:AGENT_RECORDER_API_KEY = Get-Content .\.local-data\config\api-key.txt
# Linux/macOS
export AGENT_RECORDER_API_KEY=$(cat .local-data/config/api-key.txt)
```
