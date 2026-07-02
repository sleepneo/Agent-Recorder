# Agent Recorder Release Scripts

This directory contains release-safe PowerShell scripts for AI-agent internal
adapters, local validation, and troubleshooting.

These scripts are not the normal human-user entry point. The primary workflow is:

```text
human natural language -> local AI agent -> Agent Recorder -> local confirmation UI -> MP4 output
```

All scripts are self-contained and resolve paths relative to `$PSScriptRoot`.

## Scripts

| Script | Purpose |
| --- | --- |
| `start-agent-recorder.ps1` | Start Agent Recorder tray app and wait for API readiness |
| `stop-agent-recorder.ps1` | Stop Agent Recorder tray app |
| `smoke-capabilities.ps1` | Quick smoke test: call `/api/v1/capabilities` |
| `probe-api-contract-noninteractive.ps1` | Non-interactive API contract probe |
| `record-selected-region.ps1` | Internal/validation path for a selected-region recording |
| `record-nested-regions.ps1` | Internal/validation path for a nested recording demo |
| `validate-release-script-contract.ps1` | Static contract check for the release scripts |

## API Key

After first launch, Agent Recorder writes a default API key to:

```text
<package-root>\.local-data\config\api-key.txt
```

All release scripts read the key from this file automatically. AI agents may call
these scripts directly when they want a packaged adapter instead of raw HTTP API
calls.

## Package Layout

```text
AgentRecorder/
  AgentRecorder.App/
    AgentRecorder.App.exe
    ffmpeg.exe, ffprobe.exe
    ...
  .local-data/             <- created on first run
    config/api-key.txt
    logs/audit.jsonl
    Videos/
  README.md
  README.zh-CN.md
  QUICKSTART.md
  QUICKSTART.zh-CN.md
  AGENT-INSTRUCTIONS.zh-CN.md
  AGENT-API-REFERENCE.zh-CN.md
  LICENSE
  LICENSE-NOTICE.md
```
