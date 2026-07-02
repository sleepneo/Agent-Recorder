# Agent Recorder

Agent Recorder is an **AI agent-native local screen recording capability layer**
for Windows. A local AI agent drives Agent Recorder through the raw HTTP API.

Primary flow:

```text
human natural language -> local AI agent -> Agent Recorder raw API -> local confirmation UI -> MP4 output
```

The portable package includes these agent-facing docs:

- `AGENT-INSTRUCTIONS.zh-CN.md`
- `AGENT-API-REFERENCE.zh-CN.md`
- `QUICKSTART.zh-CN.md`

## Capabilities

- Display, window, and selected-region recording.
- Local user selection and confirmation UI.
- Mandatory local confirmation before recording.
- HTTP self-approval blocked with 405.
- Nested recording: one outer recording can capture the process of starting an inner recording.
- Local audit log and MP4 output.

## Basic Usage

The human user can speak to a local AI agent, for example:

```text
Record a selected region for 30 seconds.
```

The AI agent should start `AgentRecorder.App\AgentRecorder.App.exe`, call the raw
API, wait for local confirmation, and report the final MP4 path.

## Project Layout

```text
src/
  AgentRecorder.App            WinForms tray host and local confirmation UI
  AgentRecorder.Api            localhost HTTP API
  AgentRecorder.Core           Recording state machine and contracts
  AgentRecorder.Capture        FFmpeg capture backends
  AgentRecorder.Windows        Win32 display/window helpers
  AgentRecorder.Security       Safety policy checks
  AgentRecorder.Logging        Audit log writer
tests/
  AgentRecorder.Tests
tools/
  ffmpeg/bin                   Bundled FFmpeg/ffprobe
```

## Developer Build And Test

```powershell
dotnet build AgentRecorder.sln --configuration Release --no-restore
dotnet test AgentRecorder.sln --configuration Release --no-restore
```

These commands are for source development and validation, not the normal user
flow.

## License

Agent Recorder is licensed under the MIT License. The portable package also
includes FFmpeg binaries, which remain subject to FFmpeg's own license terms;
see `LICENSE-NOTICE.md`.
