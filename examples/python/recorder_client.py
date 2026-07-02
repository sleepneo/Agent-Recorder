#!/usr/bin/env python3
"""
Agent Recorder Python 客户端示例

提供完整的录制流程功能，包括：
- 获取显示器列表
- 获取窗口列表
- 发起录制请求
- 等待用户确认
- 等待录制完成
- 获取录制状态

依赖：requests

安装依赖：
    pip install requests

使用示例：
    python recorder_client.py --duration 5
    python recorder_client.py --help
"""

import os
import time
import json
import argparse
import requests

DEFAULT_BASE_URL = "http://127.0.0.1:37891"
DEFAULT_API_KEY = os.environ.get("AGENT_RECORDER_API_KEY", None)


def _get_default_api_key():
    """获取默认 API Key，优先级：环境变量 > token 文件"""
    if DEFAULT_API_KEY:
        return DEFAULT_API_KEY

    # 尝试从 token 文件读取
    data_dir = os.environ.get("AGENT_RECORDER_DATA_DIR", ".local-data")
    config_dir = os.path.join(data_dir, "config")
    token_file = os.path.join(config_dir, "api-key.txt")

    if os.path.exists(token_file):
        try:
            with open(token_file, "r") as f:
                return f.read().strip()
        except Exception:
            pass

    return None


class AgentRecorderClient:
    """Agent Recorder API 客户端"""

    def __init__(self, base_url=DEFAULT_BASE_URL, api_key=None):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key if api_key is not None else _get_default_api_key()
        self.session = requests.Session()
        if self.api_key:
            self.session.headers["X-Agent-Recorder-Key"] = self.api_key

    def _request(self, method, endpoint, **kwargs):
        """内部请求方法"""
        url = f"{self.base_url}/api/v1/{endpoint.lstrip('/')}"
        try:
            resp = self.session.request(method, url, **kwargs)
            resp.raise_for_status()
            return resp.json()
        except requests.exceptions.RequestException as e:
            try:
                error_data = resp.json() if resp else {}
                error_msg = error_data.get("message", str(e))
                error_code = error_data.get("error", "UNKNOWN")
                raise Exception(f"API Error [{error_code}]: {error_msg}") from e
            except ValueError:
                raise Exception(f"Request failed: {e}") from e

    def get_capabilities(self):
        """获取服务能力"""
        return self._request("GET", "capabilities")

    def get_permissions(self):
        """获取权限状态"""
        return self._request("GET", "permissions")

    def get_displays(self):
        """获取显示器列表"""
        return self._request("GET", "displays")

    def get_windows(self, include_minimized=False):
        """获取窗口列表"""
        params = {"include_minimized": include_minimized}
        return self._request("GET", "windows", params=params)

    def get_active_window(self):
        """获取活动窗口"""
        return self._request("GET", "windows/active")

    def get_audio_devices(self):
        """获取音频设备"""
        return self._request("GET", "audio/devices")

    def start_recording(
        self,
        source_type,
        source_id,
        duration_seconds,
        enable_microphone=False,
        fps=30,
        quality="medium"
    ):
        """
        发起录制请求
        
        Args:
            source_type: 'display' 或 'window'
            source_id: 显示器或窗口 ID
            duration_seconds: 录制时长（秒）
            enable_microphone: 是否启用麦克风
            fps: 帧率
            quality: 视频质量 ('low', 'medium', 'high')
        
        Returns:
            响应数据，包含 confirmation_id 或 recording_id
        """
        payload = {
            "source": {"type": source_type, f"{source_type}_id": source_id},
            "audio": {"microphone": {"enabled": enable_microphone}},
            "video": {"fps": fps, "quality": quality},
            "stop_condition": {"type": "duration", "seconds": duration_seconds}
        }
        return self._request("POST", "recordings", json=payload)

    def get_recording_status(self, recording_id):
        """获取录制状态"""
        return self._request("GET", f"recordings/{recording_id}")

    def stop_recording(self, recording_id, reason="user_requested"):
        """停止录制"""
        payload = {"reason": reason}
        return self._request("POST", f"recordings/{recording_id}/stop", json=payload)

    def get_recording_list(self):
        """获取录制列表"""
        return self._request("GET", "recordings")

    def get_confirmation_status(self, confirmation_id):
        """获取确认状态"""
        return self._request("GET", f"confirmations/{confirmation_id}")


def print_json(data):
    """格式化输出 JSON"""
    print(json.dumps(data, indent=2, ensure_ascii=False))


def print_section(title):
    """打印分隔线和标题"""
    print("\n" + "=" * 60)
    print(title)
    print("=" * 60)


def main():
    parser = argparse.ArgumentParser(description="Agent Recorder Python 客户端")
    parser.add_argument("--duration", type=int, default=5, help="录制时长（秒）")
    parser.add_argument("--microphone", action="store_true", help="启用麦克风")
    parser.add_argument("--fps", type=int, default=30, choices=[15, 24, 30, 60], help="帧率")
    parser.add_argument("--quality", default="medium", choices=["low", "medium", "high"], help="视频质量")
    parser.add_argument("--window", action="store_true", help="录制窗口而非显示器")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="服务地址")
    parser.add_argument("--api-key", default=DEFAULT_API_KEY, help="API Key")
    args = parser.parse_args()

    # 创建客户端
    client = AgentRecorderClient(base_url=args.base_url, api_key=args.api_key)

    try:
        # 1. 获取服务能力
        print_section("1. 获取服务能力")
        caps = client.get_capabilities()
        print(f"服务名称: {caps['data']['app']['name']}")
        print(f"版本: {caps['data']['app']['version']}")
        print(f"认证要求: {caps['data']['auth']['required']}")

        # 2. 获取显示器或窗口
        if args.window:
            print_section("2. 获取窗口列表")
            windows = client.get_windows()
            window_list = windows["data"]["windows"]
            
            if not window_list:
                print("没有找到可用窗口")
                return
            
            print("可用窗口:")
            for i, win in enumerate(window_list[:5], 1):
                print(f"  {i}. [{win['id']}] {win['title']}")
            
            # 选择第一个窗口
            selected_id = window_list[0]["id"]
            print(f"\n选择窗口: {selected_id}")
            source_type = "window"
        else:
            print_section("2. 获取显示器列表")
            displays = client.get_displays()
            display_list = displays["data"]["displays"]
            
            if not display_list:
                print("没有找到可用显示器")
                return
            
            print("可用显示器:")
            for disp in display_list:
                print(f"  [{disp['id']}] {disp['name']} - {disp['bounds']['width']}x{disp['bounds']['height']}")
            
            # 选择主显示器
            selected_id = next(d["id"] for d in display_list if d.get("is_primary", False))
            print(f"\n选择显示器: {selected_id}")
            source_type = "display"

        # 3. 发起录制请求
        print_section("3. 发起录制请求")
        print(f"录制类型: {source_type}")
        print(f"录制时长: {args.duration} 秒")
        print(f"麦克风: {'开启' if args.microphone else '关闭'}")
        print(f"帧率: {args.fps}")
        print(f"质量: {args.quality}")

        result = client.start_recording(
            source_type=source_type,
            source_id=selected_id,
            duration_seconds=args.duration,
            enable_microphone=args.microphone,
            fps=args.fps,
            quality=args.quality
        )

        data = result["data"]
        print(f"响应状态: {data['status']}")

        # 4. 处理确认流程
        if data["status"] == "requires_user_confirmation":
            confirmation_id = data["confirmation_id"]
            print(f"\n等待用户确认... (confirmation_id: {confirmation_id})")
            print("请在弹出的对话框中点击确认")

            recording_id = None
            timeout = 30  # 30 秒超时
            start_time = time.time()

            while time.time() - start_time < timeout:
                status = client.get_confirmation_status(confirmation_id)
                confirm_status = status["data"]["status"]
                print(f"\r确认状态: {confirm_status}", end="", flush=True)

                if confirm_status == "approved":
                    recording_id = status["data"]["recording_id"]
                    print(f"\n用户已确认！录制开始 (recording_id: {recording_id})")
                    break
                elif confirm_status == "rejected":
                    print("\n用户拒绝录制")
                    return
                elif confirm_status == "expired":
                    print("\n确认超时")
                    return

                time.sleep(1)
            else:
                print("\n等待超时")
                return
        else:
            recording_id = data["recording_id"]
            print(f"录制已开始 (recording_id: {recording_id})")

        # 5. 等待录制完成
        print_section("4. 等待录制完成")
        
        while True:
            status = client.get_recording_status(recording_id)
            rec_status = status["data"]["status"]
            elapsed = status["data"].get("elapsed_seconds", 0)
            
            print(f"\r状态: {rec_status} | 已录制: {elapsed:.1f} 秒", end="", flush=True)

            if rec_status == "completed":
                print("\n录制完成！")
                output = status["data"]["output"]
                print(f"输出文件: {output['path']}")
                print(f"文件大小: {output['bytes_written']:,} 字节")
                print(f"实际时长: {output['duration_seconds']:.3f} 秒")
                break
            elif rec_status == "failed":
                print("\n录制失败")
                stderr = status["data"].get("stderr_excerpt", "")
                if stderr:
                    print(f"错误信息: {stderr}")
                return

            time.sleep(0.5)

        print_section("录制流程完成")

    except Exception as e:
        print(f"\n错误: {e}")
        import traceback
        traceback.print_exc()


if __name__ == "__main__":
    main()
