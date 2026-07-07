# Agent Recorder

Agent Recorder is an **AI agent-native local screen recording capability layer**
for Windows. A human describes what to record, a local AI agent calls the
localhost API, and Agent Recorder provides local selection, confirmation,
recording, output, and audit logs.

Primary flow:

```text
human natural language -> local AI agent -> quick API -> local selection/confirmation UI -> MP4 output
```

## Why It Exists

- Humans can ask naturally: "record the current window for 5 minutes" or
  "record a selected region for 30 seconds."
- AI agents use a small localhost API instead of controlling a traditional
  screen recorder UI.
- Agent Recorder keeps the safety boundary local: every recording requires
  visible user selection and/or confirmation.

## Recommended Agent Path

1. Start or reuse the app:

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json
```

2. Read the API key from the returned `api_key_file`.
3. Use `POST /api/v1/recordings/quick` for common intents:
   - `primary_display`
   - `active_window`
   - `selected_region`
4. Poll confirmation and recording status, then report the MP4 path.

Advanced agents may still use the lower-level API for precise display, window,
region, output, or nested-recording control.

## Capabilities

- Quick intent API for primary display, active window, and selected region.
- Lower-level display, window, and region recording APIs.
- Active-window recording uses visible window bounds clipped to the virtual
  desktop, then records via the FFmpeg `ffmpeg-window-region` backend.
- Interactive selected-region UI.
- Mandatory local user confirmation before recording starts.
- HTTP self-approval blocked with `405 METHOD_NOT_ALLOWED`.
- Nested recording: one outer recording can capture the process of starting an
  inner recording.
- User-level autostart controls and FFmpeg prewarm support.
- Local audit log and MP4 output.

## Documentation

- `QUICKSTART.md`
- `QUICKSTART.zh-CN.md`
- `AGENT-INSTRUCTIONS.zh-CN.md`
- `AGENT-API-REFERENCE.zh-CN.md`
- `docs/api.md`
- `docs/safety.md`
- `docs/agent-tool-spec.md`

## Project Layout

```text
src/
  AgentRecorder.App            WinForms tray host, selection UI, confirmation UI
  AgentRecorder.Api            localhost HTTP API
  AgentRecorder.Core           recording state machine and contracts
  AgentRecorder.Capture        FFmpeg capture backend and prewarm
  AgentRecorder.Windows        Win32 display/window helpers
  AgentRecorder.Security       safety policy checks
  AgentRecorder.Logging        audit log writer
tools/
  AgentRecorder.Cli            agent startup/autostart helper
  ffmpeg/bin                   bundled FFmpeg/ffprobe
tests/
  AgentRecorder.Tests
```

## Build And Test

```powershell
dotnet build AgentRecorder.sln --configuration Release
dotnet test tests\AgentRecorder.Tests\AgentRecorder.Tests.csproj --configuration Release --no-build --no-restore
```

## License

Agent Recorder is licensed under the MIT License. The portable package also
includes FFmpeg binaries, which remain subject to FFmpeg's own license terms;
see `LICENSE-NOTICE.md`.
