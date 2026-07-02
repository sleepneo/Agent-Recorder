# Agent Recorder - 安全说明

## 概述

Agent Recorder 专为 AI Agent 场景设计，内置多层次安全保护机制，确保录制行为的合法性和用户隐私保护。

## 安全架构

```
┌─────────────────────────────────────────────────────────────┐
│                    Agent Recorder                          │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│  │  API Key     │  │ 路径安全     │  │ 窗口黑名单   │     │
│  │  认证层      │  │  检查层      │  │  检查层      │     │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘     │
│         │                 │                 │              │
│         └────────┬────────┴────────┬────────┘              │
│                  │                 │                       │
│         ┌────────▼────────┐  ┌─────▼─────┐                │
│         │   用户确认层     │  │ 审计日志   │                │
│         │  (强制弹窗确认)   │  │           │                │
│         └────────┬────────┘  └─────┬─────┘                │
│                  │                 │                       │
│                  └────────┬────────┘                       │
│                           │                                │
│                  ┌────────▼────────┐                       │
│                  │   FFmpeg 录制   │                       │
│                  └────────────────┘                       │
└─────────────────────────────────────────────────────────────┘
```

## 安全机制详解

### 1. 网络安全

#### 1.1 本地绑定
- **实现方式**：API 服务仅监听 `http://127.0.0.1:37891/`
- **安全保障**：禁止外部网络访问，避免远程攻击
- **配置**：不支持修改监听地址

#### 1.2 API Key 认证
- **启用条件**：首次启动时自动生成 Key 并保存到本地文件
- **认证方式**：请求头 `X-Agent-Recorder-Key`
- **保护范围**：所有写操作和敏感读操作（recordings, confirmations）
- **Key 来源优先级**：
  1. 环境变量 `AGENT_RECORDER_API_KEY`
  2. 本地 token 文件 `<DataDir>/config/api-key.txt`
  3. 自动生成新的随机 Key

```powershell
# 设置 API Key（推荐生产环境使用）
$env:AGENT_RECORDER_API_KEY = "your-secure-random-key-here"
```

### 2. 用户确认机制

#### 2.1 强制确认弹窗
- **触发条件**：所有录制请求必须经过用户确认
- **弹窗内容**：
  - 录制来源（显示器/窗口名称）
  - 预计录制时长
  - 麦克风状态
  - 输出目录

#### 2.2 确认超时
- **超时时间**：30 秒
- **超时行为**：自动取消录制请求，记录审计日志

#### 2.3 录制指示器
- **视觉提示**：录制过程中托盘图标闪烁
- **用户感知**：确保用户知道正在录制

### 3. 敏感窗口黑名单

#### 3.1 黑名单关键字

**英文关键字**：
- `1password`, `bitwarden`, `keepass`
- `windows security`, `credential manager`
- `password`, `passkey`, `vault`, `keychain`
- `authy`, `duo`, `yubikey`

**中文关键字**：
- `凭据管理器`, `windows 安全中心`, `密码管理器`
- `密钥库`, `认证器`, `令牌`, `身份验证`

#### 3.2 检测机制
- **检测时机**：发起录制请求时
- **检测范围**：活动窗口标题和进程名
- **行为**：匹配到黑名单时拒绝录制，返回 `SOURCE_UNAVAILABLE` 错误

#### 3.3 误报处理
- 用户可在确认对话框中看到被阻止的原因
- 用户可关闭敏感窗口后重试

### 4. 路径安全检查

#### 4.1 禁止写入的目录

**系统目录**：
- `\Windows\`
- `\Program Files\`
- `\Program Files (x86)\`
- `\System32\`
- `\ProgramData\`
- `\Users\Public\`
- `\Users\All Users\`

**用户敏感目录**：
- `\AppData\Roaming\Microsoft\Credentials\`
- `\AppData\Roaming\Microsoft\Protect\`
- `\AppData\Local\Microsoft\Credentials\`

#### 4.2 路径规范化
- 支持绝对路径和相对路径
- 自动解析 `~`, `%USERPROFILE%` 等环境变量
- 拒绝包含 `..` 的路径遍历尝试

#### 4.3 默认输出目录
- 默认路径：`<数据目录>/Videos/`
- 可配置：通过 `AGENT_RECORDER_DATA_DIR` 环境变量

```powershell
# 自定义数据目录
$env:AGENT_RECORDER_DATA_DIR = "D:\MyRecordings"
```

### 5. 审计日志

#### 5.1 日志位置
- 文件路径：`<数据目录>/logs/audit.jsonl`
- 滚动策略：每日一个文件，保留 30 天

#### 5.2 日志事件类型

| 事件类型 | 说明 |
|----------|------|
| `recording.requested` | 录制请求发起 |
| `recording.confirmed` | 用户确认录制 |
| `recording.rejected` | 用户拒绝录制 |
| `recording.started` | 录制开始 |
| `recording.stopped` | 录制正常停止 |
| `recording.cancelled` | 录制被取消 |
| `recording.completed` | 录制完成 |
| `recording.failed` | 录制失败 |
| `confirmation.expired` | 确认超时 |
| `api.auth.failed` | 认证失败 |
| `api.access.denied` | 访问被拒绝 |

#### 5.3 日志格式

```json
{
  "timestamp": "2026-06-18T08:00:00.000Z",
  "event": "recording.started",
  "request_id": "req_abc123",
  "recording_id": "rec_xyz789",
  "details": {
    "source_type": "display",
    "source_id": "display_0",
    "duration_seconds": 5,
    "microphone_enabled": false,
    "output_path": "D:\\...\\recording.mp4"
  },
  "user_context": {
    "username": "user",
    "machine_name": "DESKTOP-ABC123"
  }
}
```

## 安全最佳实践

### 部署建议

1. **生产环境**：
   - 设置强随机 API Key
   - 定期轮换密钥
   - 监控审计日志

2. **开发环境**：
   - 可使用默认开发密钥
   - 确保开发机防火墙规则

### API 使用安全

```powershell
# 推荐：使用环境变量传递密钥
$env:AGENT_RECORDER_API_KEY = "your-key"
.\scripts\start-server.ps1

# 禁止：在命令行直接传递密钥
# .\scripts\start-server.ps1 -ApiKey "your-key"  # 不安全！
```

### 敏感信息保护

- 禁止在代码或配置文件中硬编码 API Key
- 禁止在日志中记录完整密钥值
- API Key 仅用于认证，不参与其他业务逻辑

## 安全审计清单

### 认证与授权
- [x] API Key 验证正确实现
- [x] 敏感接口需要认证
- [x] 认证失败返回正确错误码

### 网络安全
- [x] 仅绑定 localhost
- [x] 无 CORS 配置（本地服务不需要）
- [x] 端口固定为 37891

### 用户隐私
- [x] 强制用户确认机制
- [x] 录制指示器
- [x] 敏感窗口黑名单

### 路径安全
- [x] 阻止系统目录写入
- [x] 阻止敏感目录写入
- [x] 路径遍历防护

### 审计日志
- [x] 记录所有关键事件
- [x] 包含请求追踪信息
- [x] 记录用户上下文

## 安全风险评估

| 风险等级 | 风险描述 | 缓解措施 |
|----------|----------|----------|
| 高 | 远程未授权访问 | 本地绑定 + API Key 认证 |
| 高 | 静默录制攻击 | 强制用户确认弹窗 |
| 中 | 敏感信息泄露 | 窗口黑名单 + 路径安全 |
| 中 | 路径遍历攻击 | 路径规范化 + 黑名单检查 |
| 低 | 日志信息泄露 | 不记录敏感数据 |

## 应急响应

### 安全事件处理流程

1. **发现**：通过审计日志监控异常行为
2. **评估**：确认事件严重程度
3. **响应**：
   - 停止服务
   - 轮换 API Key
   - 保存日志证据
4. **恢复**：
   - 修复漏洞
   - 重启服务
5. **报告**：记录事件处理过程

### 紧急联系

如发现安全漏洞，请立即停止服务并联系开发团队。

## 更新日志

| 日期 | 版本 | 安全更新 |
|------|------|----------|
| 2026-06 | v1.0 | 初始版本，包含所有安全机制 |
