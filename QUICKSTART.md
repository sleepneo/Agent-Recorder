# Quick Start - Agent Recorder

Agent Recorder is an **AI agent-native local screen recording capability layer**.
The normal path is: human speaks, local AI agent calls the quick API, Agent
Recorder asks for local selection/confirmation, then writes an MP4.

## How To Use

1. Download and extract the Windows portable zip.
2. Ask your local AI agent to read:
   - `AGENT-INSTRUCTIONS.zh-CN.md`
   - `AGENT-API-REFERENCE.zh-CN.md`
3. Tell the agent what to record:

```text
Record a selected region for 30 seconds.
```

or:

```text
Record the current conversation window for 5 minutes.
```

4. The AI agent should:
   - run `AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json`
   - read the API key from the returned `api_key_file`
   - call `POST /api/v1/recordings/quick`
   - poll `/confirmations/{id}` until the local user approves or rejects
   - poll `/recordings/{id}` until completion
   - report the final MP4 path

5. The human user only selects the region, confirms recording locally, and plays
   the returned MP4.

## Quick API Targets

| target.type | Use case |
| --- | --- |
| `primary_display` | Record the primary display |
| `active_window` | Record the current active window using its visible bounds |
| `selected_region` | Ask the user to draw a region, then record it |

Example request:

```json
{
  "target": { "type": "selected_region", "selection_timeout_seconds": 120 },
  "duration_seconds": 30,
  "video": { "fps": 30, "quality": "medium" }
}
```

## Files

When started through `AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json`
from the portable package, the default data directory is
`<package-root>\.local-data`:

- API key: `.local-data\config\api-key.txt`
- Videos: `.local-data\Videos\`
- Audit log: `.local-data\logs\audit.jsonl`

If `AgentRecorder.App.exe` or `AgentRecorder.Headless.exe` is launched directly
without `AGENT_RECORDER_DATA_DIR`, the default data directory is
`%LOCALAPPDATA%\AgentRecorder`. Agents should trust the returned `data_dir` and
`api_key_file` fields.

## Safety

- API binds to `127.0.0.1`.
- State-changing calls require `X-Agent-Recorder-Key`.
- AI agents can request recording but cannot silently approve it.
- Local user confirmation is mandatory.
- HTTP self-approval is blocked with `405 METHOD_NOT_ALLOWED`.
