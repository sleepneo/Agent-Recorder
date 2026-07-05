# Agent Recorder curl Examples

These snippets show the raw HTTP calls a local AI agent may issue. They are API
examples, not the normal human-user path.

## Prerequisites

Start or reuse Agent Recorder:

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json
```

Read the API key from the returned `api_key_file`, then set:

```powershell
$env:AGENT_RECORDER_API_KEY = Get-Content .\.local-data\config\api-key.txt
```

## Capabilities

```powershell
curl.exe http://127.0.0.1:37891/api/v1/capabilities
```

## Quick Selected-Region Recording

```powershell
curl.exe -X POST http://127.0.0.1:37891/api/v1/recordings/quick `
  -H "X-Agent-Recorder-Key: $env:AGENT_RECORDER_API_KEY" `
  -H "X-Agent-Name: curl-example" `
  -H "Content-Type: application/json" `
  -d "{ `"target`": { `"type`": `"selected_region`", `"selection_timeout_seconds`": 120 }, `"duration_seconds`": 30, `"video`": { `"fps`": 30, `"quality`": `"medium`" } }"
```

The user will select a region and approve recording locally. The response
contains a `confirmation_id`; poll it until approved/rejected/expired.

## Quick Active-Window Recording

```powershell
curl.exe -X POST http://127.0.0.1:37891/api/v1/recordings/quick `
  -H "X-Agent-Recorder-Key: $env:AGENT_RECORDER_API_KEY" `
  -H "X-Agent-Name: curl-example" `
  -H "Content-Type: application/json" `
  -d "{ `"target`": { `"type`": `"active_window`" }, `"duration_seconds`": 60 }"
```

## Poll Confirmation

```powershell
curl.exe -H "X-Agent-Recorder-Key: $env:AGENT_RECORDER_API_KEY" `
  http://127.0.0.1:37891/api/v1/confirmations/<confirmation_id>
```

## Poll Recording

```powershell
curl.exe -H "X-Agent-Recorder-Key: $env:AGENT_RECORDER_API_KEY" `
  http://127.0.0.1:37891/api/v1/recordings/<recording_id>
```

## Stop Manual Recording

```powershell
curl.exe -X POST http://127.0.0.1:37891/api/v1/recordings/<recording_id>/stop `
  -H "X-Agent-Recorder-Key: $env:AGENT_RECORDER_API_KEY" `
  -H "Content-Type: application/json" `
  -d "{ `"reason`": `"user_requested`" }"
```

## Lower-Level Discovery

```powershell
curl.exe http://127.0.0.1:37891/api/v1/displays
curl.exe http://127.0.0.1:37891/api/v1/windows
curl.exe http://127.0.0.1:37891/api/v1/windows/active
```

Use lower-level endpoints only when the agent needs exact source control beyond
the quick API targets.
