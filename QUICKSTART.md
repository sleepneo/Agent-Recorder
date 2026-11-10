# Quick Start - Agent Recorder

> This guide describes v0.1.21. New video requests deliver video only; no extra instruction is needed. Explicit artifacts.bundle=true is rejected. Legacy Bundle history remains protected. Screenshot-series images and their manifest are unchanged.

Agent Recorder is an **AI agent-native local screen recording capability layer**.
The normal path is: human speaks, local AI agent calls the quick API, Agent
Recorder asks for local selection/confirmation, then writes an MP4.

## How To Use

1. Extract the v0.1.21 Windows x64 portable ZIP. When reusing data from an earlier version, back it up before first launch; the database upgrades to SQLite V51.
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
   - call `POST /api/v1/recordings/quick?wait_for=recording&wait_ms=25000`
   - treat `wait.goal_reached=true` as the trusted start signal
   - if the bounded wait times out, continue with recording long-polling
   - wait for completion
   - report the final MP4 path

5. The human user only selects the region, confirms recording locally, and plays
   the returned MP4. In the selection UI, drag a custom rectangle or click a
   highlighted window; edges snap automatically, and holding `Alt` temporarily
   disables snapping for precise adjustment.

## Recording Audio

**Audio is off unless requested.** "Record the entire window" does not
automatically include the sound it plays. Tell your AI agent explicitly:

```text
Find PotPlayer and record its entire window for 20 seconds with system audio, without the microphone.
Record a selected region for 15 seconds using the AirPods microphone, without system audio.
Record the primary display for 15 seconds without any audio.
```

- **System audio** includes all sound on the selected output device, potentially
  including other applications; it is not a window-exclusive audio track.
- **Microphone** capture is supported for ordinary region and display video,
  not window-content recording.
- **System audio and microphone cannot be enabled together.** Raw fixed-region
  profile execution remains silent; ordinary quick region-only reuse can choose
  audio for this recording. Fixed-region unattended plans and screenshot
  sequences remain silent.
- With no device specified, use the current Windows multimedia default output
  or a uniquely determined microphone input. The locally approved device stays
  fixed; changing the default does not switch capture to another device.
- Check the audio mode and device before approving. If requested audio is
  unavailable, the agent must explain it rather than silently record without it.

See [Recording Audio in the README](README.md#recording-audio) for the full rules.

## Nested Recording Test

Tell your AI agent:

```text
Start an outer selected-region recording for 120 seconds.
```

Select the outer region, approve locally, and wait for REC. Then immediately say:

```text
While outer continues recording, start an inner selected-region recording for 20 seconds.
```

Select the inner region and approve locally. The 120-second outer duration
leaves room for agent responses and both interactions; it does not guarantee
a fixed response time. To see the inner recording controls in the outer video,
select regions on the same display with the inner region and controls inside
the outer region.

Check that outer continues after inner finishes and that both videos are valid.
If outer has ended before inner is created, the agent must explain that no
nested recording was established rather than report a successful nested test.

## One-Shot Unattended Test

Complete setup before starting the countdown. First tell your AI agent:

```text
Check whether limited unattended recording is enabled. If it is disabled, tell me where to enable it, but do not create a plan yet.
```

If disabled, open the unattended safety controls from the Recorder tray menu
and enable the feature locally. Ask the agent to check the current instance
again. Once it is enabled, send a new instruction:

```text
In two minutes, automatically record a fixed region for 10 seconds without audio. Let me select the region and approve a one-shot authorization now; do not ask again at execution time.
```

The two-minute delay belongs to this new instruction after setup, not the time
spent enabling the switch. Select the region and approve its authorization
locally. The agent should report the specific scheduled start time and whether
scheduling succeeded. Enabling the global switch does not authorize a specific
recording; waiting for the switch or approval is not a scheduled plan.

Keep Recorder running and the computer awake with the desktop unlocked.
Check that recording starts without a second confirmation and ends after about
10 seconds. An expired approval or missed execution window must be reported,
not silently postponed or retried.

## Quick API Targets

| target.type | Use case |
| --- | --- |
| `primary_display` | Record the primary display |
| `windows_display` | Record the positive display number shown by Windows “Identify” |
| `active_window` | Fix the active window identity and record its own content, excluding obscuring windows |
| `selected_region` | Ask the user to draw a region, then record it |
| `last_region` | Reuse the most recent successful region selection |

Window video captures the window's own content, including its controls, even
when another application covers it. Use 1–1800 whole seconds with no audio or
system audio; microphone capture is not supported. Independent silent windows
use separate raw requests, rather than this quick request.
Unavailable window capture fails explicitly instead of recording the desktop.
For detailed limits, see [the API reference](docs/api.md#strict-wgc-window-surface-recording).

Example request:

```json
{
  "target": { "type": "selected_region", "selection_timeout_seconds": 120 },
  "duration_seconds": 30,
  "countdown_seconds": 3,
  "video": { "fps": 30, "quality": "medium" }
}
```

For `windows_display`, send a positive JSON integer such as
`"windows_display_number": 2`. It is the number shown by Windows “Identify”,
not the suffix of an API `display_id`. Local confirmation is still mandatory.

The optional bounded creation wait observes local approval, preparation,
countdown, and the trusted first frame without approving the request. If it
returns `timed_out=true`, use the returned `recording_id` to continue with
`GET /recordings/{id}` long-polling. Omitting `wait_for` preserves the immediate
creation response.

For a recording that should start later without another prompt at start time,
ask the agent for a one-shot or bounded daily/weekly plan. Enable unattended
mode in the local tray safety controls, then select a fixed region and approve
that plan's lease locally. The agent can observe setup status but cannot grant
the lease over HTTP. See `docs/api.md` for the plan request and limits.

To decide at execution time instead, ask for a one-time `required` plan.
After local region selection and plan-creation approval, a second local dialog
must be approved when the run is due. Read setup progress from
`/plan-setups/{id}` and durable execution status from
`/plans/{plan_id}/status`; a scheduled plan is not a completed recording.

`countdown_seconds` is optional for both raw and quick recording requests. It
defaults to `3` and accepts only integer values `0` through `10`. Use `0` to
skip the visible countdown while keeping local confirmation, preparation,
preflight, and credible-first-frame gating. The value is shown in the local
confirmation summary and returned under recording `config`.

## Saved Regions And Future Windows

Ask the agent to save a named recording region when the same area is used
often. Select it locally once; later recordings use the exact saved version
and still need local confirmation. Ask to reuse its region with this request's
1-600 second duration and optional system audio or microphone. The agent uses
quick `profile_region`; raw `profile_ref` retains the full silent specification.
Changed display geometry is rejected rather than silently adjusted.

For a window opened later, the agent can request a one-shot authorization for
an exact local executable, output directory, and optional fixed system-audio
device. Enable unattended mode and approve that bounded grant locally. The
agent then opens the program, identifies its eligible window, and requests the
single run. Grants last at most one hour; each run is at most 30 minutes.
Recorder does not open video platforms or recognize livestreams.

## Files

The installed edition uses `%LOCALAPPDATA%\AgentRecorder` for data by default.
The agent can discover and start it without asking the user to run PowerShell.
See [Runtime Data](README.md#runtime-data) for discovery, overrides, and
upgrade and uninstall precautions.

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
- Ordinary recordings need local confirmation; unattended plans need separate
  local fixed-region selection and lease approval. The ordinary confirmation
  window also lets the user choose the save directory for that recording.
- One-time `required` plans have no Lease and require separate local approvals
  at creation and execution.
- HTTP self-approval is blocked with `405 METHOD_NOT_ALLOWED`.

Before showing confirmation and again before capture starts, Agent Recorder
checks the output path, free space, encoder availability, capture bounds, and
whether the selected source still exists.

## Stopping Recordings

While a recording is active, the tray icon turns red and shows the recording
state. You can stop recordings in three ways:

1. **Floating stop button**: a small red stop button appears near the top-right
   corner of each recording region. Click it to stop only that recording.
2. **Tray menu**: right-click the tray icon and choose "Stop recording" (one
   active recording) or "Stop all recordings (N)" (multiple recordings).
3. **Global hotkey**: press `Ctrl+Shift+F10` to stop all active recordings.

For ordinary recordings, the REC border and floating stop button remain visible
to the user while being excluded from the captured video on a best-effort basis.
During nested recording, a geometrically safe inner control can remain visible
in the outer video so the outer recording captures the complete inner workflow.
The stop control may move inside the recording boundary when the preferred
outside position would leave its display; capture exclusion behavior is unchanged.

The agent can also stop a specific recording through the API:

```http
POST /api/v1/recordings/{recording_id}/stop
Content-Type: application/json
X-Agent-Recorder-Key: <api-key>

{
  "reason": "user_requested"
}
```

The tray menu provides a Chinese/English language selector. The choice is
persisted locally and applies to newly opened selection, confirmation, and
recording-control windows.

## Public System Audio

System audio is public and still requires the same per-run local confirmation as every ordinary
recording. A quick request can use the current Windows multimedia output:

```json
{
  "target": { "type": "selected_region" },
  "duration_seconds": 30,
  "audio": { "system_audio": { "enabled": true } }
}
```

The raw endpoint uses the same audio object:

```json
{
  "source": { "type": "display", "display_id": "display_1" },
  "stop_condition": { "type": "duration", "seconds": 30 },
  "audio": { "system_audio": { "enabled": true } }
}
```

Omit `audio.system_audio.device_id` to use the current Windows multimedia
output. For an explicit endpoint, use an exact `id` from
`GET /api/v1/audio/devices` → `output_devices`; only active render endpoints
are listed. Microphone and system audio are mutually exclusive. The endpoint
shown in local confirmation is fixed for that recording: changing the Windows
default output does not switch capture, and returning to the approved endpoint
uses bounded recovery with any silence gap reported in continuity metadata.

## Bounded Screenshot Series

Use the same quick endpoint for a bounded PNG sequence. The local user still
confirms the request:

```json
{
  "target": { "type": "selected_region" },
  "mode": "screenshot_series",
  "interval_ms": 5000,
  "max_count": 12,
  "countdown_seconds": 3
}
```

The response exposes `mode: "screenshot_series"` and the planned/captured
counts. The final output is a directory containing numbered PNG files and
`series.json`; audio and chapter marks are intentionally unsupported. Use
`max_duration_seconds` instead of `max_count` when a duration bound is more
natural, but never send both.
The schedule is anchored at the first valid PNG submission. Each scheduled
point is claimed serially and launches one bounded FFmpeg single-frame process;
there is no parallel catch-up. In `series.json`, `lateness_ms` measures delay at
the claim point, while `capture_duration_ms` measures the monotonic time from
claim to valid PNG submission. Neither is a fixed desktop-latency guarantee.

## Portable Package Contents

```text
AgentRecorder.App\                 desktop application and local UI
AgentRecorder.Headless\            advanced non-interactive service host
AgentRecorder.Cli\                 agent startup/readiness helper
AgentRecorder.AudioHelper\         isolated Windows WASAPI audio helper
README.md                          English overview
QUICKSTART.md                      this guide
AGENT-INSTRUCTIONS.zh-CN.md        agent operating instructions
AGENT-API-REFERENCE.zh-CN.md       agent API reference
LICENSE                            Agent Recorder MIT license
LICENSE-NOTICE.md                  third-party license notices
```


## Agent-requested idle shutdown

When the user asks to exit Recorder, the agent runs:

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe shutdown --json --data-dir "<data_dir returned by ensure-running>"
```

Check `capabilities.host_shutdown.supported` first. This build supports authenticated idle-only `POST /api/v1/host/shutdown`, with no force option. The CLI never starts an app or accepts a PID; it verifies ready/capabilities, user, executable, process start identity and instance nonce, sends the existing key, and waits up to 15000 ms (`--timeout-ms`, 100–120000) for that original process to exit. `exited` is confirmed process termination; `not_running` means no ready file and no held host instance. `busy`, `unsupported`, `timeout` and `unknown` are not successful exits. A 202 API `accepted` response alone does not mean exited.

Active confirmation/selection/authorization, preparation/countdown/recording/finalization, deletion and unretired or unknown resources return 409 `HOST_BUSY` with a stable `error.details.reason_code`; service admission stays open. To stop an active recording, use its existing stop API only when authorized, wait for resources, then request exit. Exiting Recorder does not authorize stopping recordings, revoking saved plans/grants, restarting or replaying work. No process killing or tray automation is part of this command. After a confirmed exit, a separately requested restart uses the usual `ensure-running --json --app "<desired build>" --data-dir "<same directory>"`.

The tray App host supports this exit operation. Other hosts, including Headless, advertise `supported=false`; the CLI returns `unsupported` without falling back to an unauthenticated event, process kill or UI automation.


## Saved Regions

Ask your agent to reuse a saved region with this recording's duration and either system audio or microphone. The agent uses quick `target.type=profile_region` with the exact Profile ref; it does not modify the saved configuration. Each call still requires local confirmation. Omitted audio means silence and countdown defaults to 3 seconds; supported default-device resolution avoids requiring a device name every time. Raw `profile_ref` retains its full silent-configuration semantics. See the [API contract](docs/api.md#quick-recording-from-a-saved-region-profile_region).


### Pixel-rate admission budget

Independent ordinary raw, silent, strict WGC windows also use the startup-frozen `AGENT_RECORDER_MAX_INDEPENDENT_WINDOW_PIXELS_PER_SECOND`. The default is `497664000` pixels/second, the arithmetic cost of two 3840×2160×30 windows. This is an initial software policy, not measured dual-4K/ten-window capacity or a performance guarantee. Explicit values must be positive ASCII decimal Int64 integers (maximum 9223372036854775807); empty, whitespace, signs, fractions and overflow are invalid. Invalid count or pixel configuration disables this branch and returns `503 INDEPENDENT_WINDOW_CONFIGURATION_INVALID` with `details.environment_variable`; other contracts remain separate. HTTP fields, confirmation callbacks and later environment changes cannot override it.

Each recording freezes the trusted capture plan's physical window surface width × height × configured FPS (15/24/30/60), including obscured/off-display surface pixels. DIP, clipped desktop rectangles and request-supplied costs are not used. Arithmetic is checked Int64. Missing/invalid plan facts or multiplication overflow return `400 INDEPENDENT_WINDOW_CAPTURE_PLAN_INVALID` before confirmation/output creation. Existing approval-time geometry revalidation still rejects changes; no automatic scaling, frame-rate reduction, target substitution or desktop fallback occurs.

Both count and pixel limits must hold; equality is allowed. Admission checks configuration, mixed contracts, count, same window, then pixel budget; invalid plan facts fail when cost is calculated. Existing request, capability and output validation still applies. Pixel excess returns `409 INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED`; details include `reason_code=independent_window_pixel_budget_exceeded`, `requested_pixels_per_second`, `reserved_pixels_per_second`, `configured_pixels_per_second` and `remaining_pixels_per_second`, without paths, HWNDs or authorization material. A final Engine-lock check atomically admits the exact Recording, frozen cost and output claim before output directory creation, confirmation or backend creation.

`recording.window_surface.independent_windows.pixel_rate_budget` reports `unit=pixels_per_second`, `policy_source`, `environment_variable`, `default_pixels_per_second`, `configured_pixels_per_second`, `configuration_valid`, `reserved_pixels_per_second`, `finalizing_or_unknown_pixels_per_second` and `remaining_pixels_per_second`. Count and pixel values share one consistent snapshot. Invalid configured values and unavailable remaining amounts are null. A snapshot reserves nothing. Pending confirmation through finalizing, terminal runs without positive retirement, and incomplete initial output/history registration retain the same reservation. Rollback and retirement release only that recording's cost.

Scope is the current Engine's independent windows and unretired delivery, not a machine-wide GPU/encoder/CPU/disk budget. `snapshot_is_reservation=false`, `performance_guaranteed=false`, `hardware_capacity_measured=false`, and `stress_tested=false`. Default count 2 and explicit count 1–10 remain unchanged. No additional quick/Profile/nested/audio/region/mixed-target/screenshot-series/Plan/Lease/future-window concurrency is enabled. New videos still deliver video only; legacy Bundle history remains protected.
