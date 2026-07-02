# Agent Recorder - Agent Tool Specification

本规范定义了 AI Agent 与 Agent Recorder 交互的工具接口。

## Tool: start_recording

### 功能描述

启动屏幕录制。支持录制整个显示器、特定窗口或自定义区域。

### 参数

| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| source_type | string | 是 | `display`、`window` 或 `region` |
| source_id | string | 是* | 显示器 ID 或窗口 ID（region 类型不需要） |
| display_id | string | 否* | 区域录制所在的显示器 ID（region 类型必填） |
| region_bounds | object | 否* | 区域边界（region 类型必填），包含 x/y/width/height |
| coordinate_space | string | 否 | 坐标系，默认 `virtual_screen`（仅 region 类型） |
| duration_seconds | int | 是 | 录制时长（秒），范围 1-7200 |
| enable_microphone | bool | 否 | 是否启用麦克风，默认 false |
| fps | int | 否 | 帧率，默认 30，可选 15/24/30/60 |
| quality | string | 否 | 视频质量，默认 `medium`，可选 `low`/`medium`/`high` |

*source_id 用于 display/window 类型；display_id + region_bounds 用于 region 类型。

### 返回值

| 字段 | 类型 | 说明 |
|------|------|------|
| recording_id | string | 录制 ID |
| status | string | `requires_confirmation` 或 `recording` |
| confirmation_id | string | 确认 ID（需要确认时返回） |
| output_path | string | 输出文件路径（录制开始后返回） |

### 工具定义（JSON Schema）

```json
{
  "name": "start_recording",
  "description": "启动屏幕录制。需要用户确认才能开始录制。",
  "parameters": {
    "type": "object",
    "properties": {
      "source_type": {
        "type": "string",
        "enum": ["display", "window", "region"],
        "description": "录制源类型：display（显示器）、window（窗口）或 region（区域）"
      },
      "source_id": {
        "type": "string",
        "description": "录制源 ID（display 或 window 类型），通过 list_displays 或 list_windows 获取"
      },
      "display_id": {
        "type": "string",
        "description": "区域录制所在的显示器 ID（region 类型必填）"
      },
      "region_bounds": {
        "type": "object",
        "description": "区域边界（region 类型必填）",
        "properties": {
          "x": { "type": "integer", "description": "区域左上角 X 坐标" },
          "y": { "type": "integer", "description": "区域左上角 Y 坐标" },
          "width": { "type": "integer", "minimum": 32, "description": "区域宽度（像素），必须为偶数" },
          "height": { "type": "integer", "minimum": 32, "description": "区域高度（像素），必须为偶数" }
        },
        "required": ["x", "y", "width", "height"]
      },
      "coordinate_space": {
        "type": "string",
        "enum": ["virtual_screen"],
        "default": "virtual_screen",
        "description": "坐标系，目前仅支持 virtual_screen"
      },
      "duration_seconds": {
        "type": "integer",
        "minimum": 1,
        "maximum": 7200,
        "description": "录制时长（秒），最大 2 小时"
      },
      "enable_microphone": {
        "type": "boolean",
        "default": false,
        "description": "是否启用麦克风录音"
      },
      "fps": {
        "type": "integer",
        "enum": [15, 24, 30, 60],
        "default": 30,
        "description": "帧率"
      },
      "quality": {
        "type": "string",
        "enum": ["low", "medium", "high"],
        "default": "medium",
        "description": "视频质量"
      }
    },
    "required": ["source_type", "source_id", "duration_seconds"]
  },
  "output": {
    "type": "object",
    "properties": {
      "recording_id": { "type": "string" },
      "status": { "type": "string" },
      "confirmation_id": { "type": "string" },
      "output_path": { "type": "string" }
    }
  },
  "safety": {
    "requires_confirmation": true,
    "description": "录制前必须获得用户同意，用户会看到录制确认对话框"
  }
}
```

## Tool: stop_recording

### 功能描述

停止正在进行的录制。

### 参数

| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| recording_id | string | 是 | 录制 ID |

### 返回值

| 字段 | 类型 | 说明 |
|------|------|------|
| recording_id | string | 录制 ID |
| status | string | `completed` 或 `failed` |
| output_path | string | 输出文件路径 |
| duration_seconds | float | 实际录制时长 |
| size_bytes | integer | 文件大小（字节） |

### 工具定义（JSON Schema）

```json
{
  "name": "stop_recording",
  "description": "停止正在进行的录制",
  "parameters": {
    "type": "object",
    "properties": {
      "recording_id": {
        "type": "string",
        "description": "录制 ID"
      }
    },
    "required": ["recording_id"]
  },
  "output": {
    "type": "object",
    "properties": {
      "recording_id": { "type": "string" },
      "status": { "type": "string" },
      "output_path": { "type": "string" },
      "duration_seconds": { "type": "number" },
      "size_bytes": { "type": "integer" }
    }
  }
}
```

## Tool: list_displays

### 功能描述

获取可用的显示器列表。

### 参数

无

### 返回值

| 字段 | 类型 | 说明 |
|------|------|------|
| displays | array | 显示器列表 |
| displays[].id | string | 显示器 ID |
| displays[].name | string | 显示器名称 |
| displays[].width | int | 宽度（像素） |
| displays[].height | int | 高度（像素） |
| displays[].is_primary | bool | 是否为主显示器 |

### 工具定义（JSON Schema）

```json
{
  "name": "list_displays",
  "description": "获取可用的显示器列表",
  "parameters": { "type": "object", "properties": {} },
  "output": {
    "type": "object",
    "properties": {
      "displays": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "name": { "type": "string" },
            "width": { "type": "integer" },
            "height": { "type": "integer" },
            "is_primary": { "type": "boolean" }
          }
        }
      }
    }
  }
}
```

## Capabilities

### 当前支持的能力

Agent Recorder 支持以下录制能力：

#### 录制源

| 源类型 | 说明 | 后端 |
|--------|------|------|
| `display` | 整个显示器 | FFmpeg gdigrab |
| `window` | 特定窗口 | FFmpeg gdigrab 或 WGC |
| `region` | 自定义区域 | FFmpeg gdigrab (desktop + offset) |

#### 录制参数

- **时长**: 1-7200 秒（2小时）
- **帧率**: 15, 24, 30, 60 fps
- **质量**: low (crf=28), medium (crf=23), high (crf=18)
- **容器**: MP4 (H.264/AAC)
- **音频**: 麦克风（可选）

#### Region 录制约束

- 区域坐标基于虚拟屏幕坐标系
- 宽高必须 >= 32x32 像素
- 宽高必须为偶数（x264/yuv420p 要求）
- 奇数宽高会自动归一化为偶数

## Tool: list_windows

### 功能描述

获取当前打开的窗口列表。

### 参数

| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| include_minimized | bool | 否 | 是否包含最小化窗口，默认 false |

### 返回值

| 字段 | 类型 | 说明 |
|------|------|------|
| windows | array | 窗口列表 |
| windows[].id | string | 窗口 ID |
| windows[].title | string | 窗口标题 |
| windows[].process_name | string | 进程名称 |
| windows[].width | int | 宽度（像素） |
| windows[].height | int | 高度（像素） |

### 工具定义（JSON Schema）

```json
{
  "name": "list_windows",
  "description": "获取当前打开的窗口列表",
  "parameters": {
    "type": "object",
    "properties": {
      "include_minimized": {
        "type": "boolean",
        "default": false,
        "description": "是否包含最小化窗口"
      }
    }
  },
  "output": {
    "type": "object",
    "properties": {
      "windows": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "name": { "type": "string" },
            "title": { "type": "string" },
            "process_name": { "type": "string" },
            "width": { "type": "integer" },
            "height": { "type": "integer" }
          }
        }
      }
    }
  }
}
```

## Tool: get_recording_status

### 功能描述

获取录制状态。

### 参数

| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| recording_id | string | 是 | 录制 ID |

### 返回值

| 字段 | 类型 | 说明 |
|------|------|------|
| recording_id | string | 录制 ID |
| status | string | 状态：`pending_confirmation`, `recording`, `completed`, `failed`, `cancelled`, `rejected`, `expired` |
| output_path | string | 输出路径（完成后） |
| elapsed_seconds | float | 已录制时长 |

### 工具定义（JSON Schema）

```json
{
  "name": "get_recording_status",
  "description": "获取录制状态",
  "parameters": {
    "type": "object",
    "properties": {
      "recording_id": {
        "type": "string",
        "description": "录制 ID"
      }
    },
    "required": ["recording_id"]
  },
  "output": {
    "type": "object",
    "properties": {
      "recording_id": { "type": "string" },
      "status": { "type": "string" },
      "output_path": { "type": "string" },
      "elapsed_seconds": { "type": "number" }
    }
  }
}
```

## Tool: get_confirmation_status

### 功能描述

查询用户确认状态。

### 参数

| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| confirmation_id | string | 是 | 确认 ID |

### 返回值

| 字段 | 类型 | 说明 |
|------|------|------|
| confirmation_id | string | 确认 ID |
| status | string | 状态：`pending`, `approved`, `rejected`, `expired` |
| recording_id | string | 录制 ID（批准后返回） |

### 工具定义（JSON Schema）

```json
{
  "name": "get_confirmation_status",
  "description": "查询用户确认状态",
  "parameters": {
    "type": "object",
    "properties": {
      "confirmation_id": {
        "type": "string",
        "description": "确认 ID"
      }
    },
    "required": ["confirmation_id"]
  },
  "output": {
    "type": "object",
    "properties": {
      "confirmation_id": { "type": "string" },
      "status": { "type": "string" },
      "recording_id": { "type": "string" }
    }
  }
}
```

## 完整工具列表（JSON）

```json
[
  {
    "name": "start_recording",
    "description": "启动屏幕录制。需要用户确认才能开始录制。支持录制整个显示器、特定窗口或自定义区域。",
    "parameters": {
      "type": "object",
      "properties": {
        "source_type": { "type": "string", "enum": ["display", "window", "region"] },
        "source_id": { "type": "string" },
        "display_id": { "type": "string" },
        "region_bounds": {
          "type": "object",
          "properties": {
            "x": { "type": "integer" },
            "y": { "type": "integer" },
            "width": { "type": "integer", "minimum": 32 },
            "height": { "type": "integer", "minimum": 32 }
          }
        },
        "coordinate_space": { "type": "string", "enum": ["virtual_screen"] },
        "duration_seconds": { "type": "integer", "minimum": 1, "maximum": 7200 },
        "enable_microphone": { "type": "boolean", "default": false },
        "fps": { "type": "integer", "enum": [15, 24, 30, 60], "default": 30 },
        "quality": { "type": "string", "enum": ["low", "medium", "high"], "default": "medium" }
      },
      "required": ["source_type", "duration_seconds"]
    }
  },
  {
    "name": "stop_recording",
    "description": "停止正在进行的录制",
    "parameters": {
      "type": "object",
      "properties": {
        "recording_id": { "type": "string" }
      },
      "required": ["recording_id"]
    }
  },
  {
    "name": "list_displays",
    "description": "获取可用的显示器列表",
    "parameters": { "type": "object", "properties": {} }
  },
  {
    "name": "list_windows",
    "description": "获取当前打开的窗口列表",
    "parameters": {
      "type": "object",
      "properties": {
        "include_minimized": { "type": "boolean", "default": false }
      }
    }
  },
  {
    "name": "get_recording_status",
    "description": "获取录制状态",
    "parameters": {
      "type": "object",
      "properties": {
        "recording_id": { "type": "string" }
      },
      "required": ["recording_id"]
    }
  },
  {
    "name": "get_confirmation_status",
    "description": "查询用户确认状态",
    "parameters": {
      "type": "object",
      "properties": {
        "confirmation_id": { "type": "string" }
      },
      "required": ["confirmation_id"]
    }
  }
]
```

## 使用流程

### 典型录制流程

```
1. 调用 list_displays() 获取显示器列表
   └─ 获取 display_id: "display_0"

2. 调用 start_recording() 发起录制请求
   └─ 返回: { "status": "requires_confirmation", "confirmation_id": "confirm_abc" }

3. 轮询 get_confirmation_status() 等待用户确认
   └─ 用户点击确认后返回: { "status": "approved", "recording_id": "rec_xyz" }

4. 轮询 get_recording_status() 等待录制完成
   └─ 返回: { "status": "completed", "output_path": "D:\\...\\video.mp4" }

5. 使用 output_path 处理录制文件
```

### 错误处理

| 错误场景 | 处理方式 |
|----------|----------|
| 用户拒绝录制 | 捕获错误，提示用户拒绝 |
| 录制超时 | 检查状态为 `expired`，重新发起请求 |
| 敏感窗口阻止 | 提示用户关闭敏感窗口后重试 |
| API Key 无效 | 检查环境变量配置 |

## 安全约束

1. **用户确认**：所有录制必须用户手动确认，禁止静默录制
2. **敏感窗口黑名单**：密码管理器、安全软件等窗口无法录制
3. **路径安全**：录制文件只能保存到允许的目录
4. **本地绑定**：API 仅监听 localhost，不对外暴露
5. **审计日志**：所有操作记录到审计日志

## 版本控制

| 版本 | 变更 | 日期 |
|------|------|------|
| v1.0 | 初始版本 | 2026-06 |
| v1.1 | 新增 region 录制源支持 | 2026-06 |
