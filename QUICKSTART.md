# Quick Start - Agent Recorder

Agent Recorder is an **AI agent-native local screen recording capability layer**.
A local AI agent should drive the recording flow through the raw HTTP API.

## How To Use

1. Download and extract the Windows portable zip.
2. Ask your local AI agent to read:
   - `AGENT-INSTRUCTIONS.zh-CN.md`
   - `AGENT-API-REFERENCE.zh-CN.md`
3. Tell the agent what to record in natural language:

```text
Record a selected region for 30 seconds.
```

4. The AI agent should:
   - start `AgentRecorder.App\AgentRecorder.App.exe`
   - set `AGENT_RECORDER_DATA_DIR=<package-root>\.local-data`
   - wait for `GET /api/v1/capabilities`
   - read `.local-data\config\api-key.txt`
   - call the raw API endpoints for region selection, recording, confirmation polling, and completion polling
   - report the final MP4 path

5. The human user only selects the region, confirms recording locally, and plays the returned MP4.

## Safety

- API binds to `127.0.0.1`.
- State-changing calls require `X-Agent-Recorder-Key`.
- AI agents can request recording but cannot silently approve it.
- Local user confirmation is mandatory.
- HTTP self-approval is blocked with 405.
