#!/usr/bin/env python3
"""
Minimal Agent Recorder Python client.

This example uses POST /api/v1/recordings/quick. It is meant as a readable API
sample for local AI-agent experiments; it does not approve confirmations.
"""

import argparse
import json
import os
import time
from pathlib import Path

import requests


DEFAULT_BASE_URL = "http://127.0.0.1:37891"


def default_api_key() -> str | None:
    env_key = os.environ.get("AGENT_RECORDER_API_KEY")
    if env_key:
        return env_key

    data_dir = Path(os.environ.get("AGENT_RECORDER_DATA_DIR", ".local-data"))
    key_file = data_dir / "config" / "api-key.txt"
    if key_file.exists():
        return key_file.read_text(encoding="utf-8").strip()
    return None


class AgentRecorderClient:
    def __init__(self, base_url: str = DEFAULT_BASE_URL, api_key: str | None = None):
        self.base_url = base_url.rstrip("/")
        self.session = requests.Session()
        key = api_key or default_api_key()
        if key:
            self.session.headers["X-Agent-Recorder-Key"] = key
        self.session.headers["X-Agent-Name"] = "python-example"

    def request(self, method: str, endpoint: str, **kwargs):
        url = f"{self.base_url}/api/v1/{endpoint.lstrip('/')}"
        response = self.session.request(method, url, timeout=10, **kwargs)
        data = response.json()
        if not response.ok or not data.get("ok", False):
            error = data.get("error", {})
            code = error.get("code", response.status_code)
            message = error.get("message", response.text)
            raise RuntimeError(f"API error {code}: {message}")
        return data

    def capabilities(self):
        return self.request("GET", "capabilities")

    def quick_recording(
        self,
        target_type: str,
        duration_seconds: int | None,
        fps: int,
        quality: str,
        selection_timeout_seconds: int,
    ):
        target = {"type": target_type}
        if target_type == "selected_region":
            target["selection_timeout_seconds"] = selection_timeout_seconds

        payload = {
            "target": target,
            "video": {"fps": fps, "quality": quality},
            "audio": {"microphone": {"enabled": False}},
        }
        if duration_seconds:
            payload["duration_seconds"] = duration_seconds
        return self.request("POST", "recordings/quick", json=payload)

    def confirmation(self, confirmation_id: str):
        return self.request("GET", f"confirmations/{confirmation_id}")

    def recording(self, recording_id: str):
        return self.request("GET", f"recordings/{recording_id}")


def dump(data):
    print(json.dumps(data, ensure_ascii=False, indent=2))


def main():
    parser = argparse.ArgumentParser(description="Agent Recorder quick API example")
    parser.add_argument(
        "--target",
        choices=["selected_region", "active_window", "primary_display"],
        default="selected_region",
    )
    parser.add_argument("--duration", type=int, default=30)
    parser.add_argument("--selection-timeout", type=int, default=120)
    parser.add_argument("--fps", type=int, choices=[15, 24, 30, 60], default=30)
    parser.add_argument("--quality", choices=["low", "medium", "high"], default="medium")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--api-key", default=None)
    args = parser.parse_args()

    client = AgentRecorderClient(args.base_url, args.api_key)

    print("Checking capabilities...")
    caps = client.capabilities()
    quick = caps["data"].get("interaction", {}).get("quick_recording_supported")
    print(f"quick_recording_supported={quick}")

    print("Requesting recording...")
    result = client.quick_recording(
        target_type=args.target,
        duration_seconds=args.duration,
        fps=args.fps,
        quality=args.quality,
        selection_timeout_seconds=args.selection_timeout,
    )
    dump(result)

    data = result["data"]
    if data.get("quick", {}).get("recording_created") is False:
        print(f"No recording was created: {data.get('status')}")
        return

    confirmation_id = data.get("confirmation_id")
    if not confirmation_id:
        print("No confirmation id returned.")
        return

    print("Waiting for local user confirmation...")
    recording_id = None
    while True:
        status = client.confirmation(confirmation_id)["data"]
        if status["status"] == "approved":
            recording_id = status["recording_id"]
            print(f"Approved. recording_id={recording_id}")
            break
        if status["status"] in {"rejected", "expired"}:
            print(f"Recording did not start: {status['status']}")
            return
        time.sleep(0.5)

    print("Waiting for recording completion...")
    while True:
        status = client.recording(recording_id)["data"]
        state = status["status"]
        if state == "completed":
            output = status.get("output", {})
            print(f"Completed: {output.get('path')}")
            return
        if state in {"failed", "cancelled", "rejected", "expired"}:
            print(f"Recording ended with state={state}")
            dump(status)
            return
        time.sleep(1)


if __name__ == "__main__":
    main()
