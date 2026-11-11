# Agent Recorder

> This guide describes v0.1.23. New recordings deliver video only. Inspect the running host's capabilities when using an older package.

Agent Recorder is an **AI agent-native local screen recording capability layer**
for Windows. A human describes what to record, a local AI agent calls the
localhost API, and Agent Recorder provides local selection, confirmation,
recording, output, and audit logs.

Primary flow:

```text
human natural language -> local AI agent -> quick API -> local selection/confirmation UI -> MP4 output
```

When neither `output.filename` nor `output.filename_template` is specified, a new video uses `recording-{datetime}-{id}.mp4` with its existing recording ID. This distinguishes automatic names across recordings; it is not a promise of permanent global uniqueness. Explicit names/templates and conflict policies remain unchanged. Changing the save directory does not regenerate the timestamp or recording ID; existing-file conflicts still follow the selected policy.

## Why It Exists

- Humans can ask naturally: "record the current window for 5 minutes" or
  "record a selected region for 30 seconds."
- AI agents use a small localhost API instead of controlling a traditional
  screen recorder UI.
- Agent Recorder keeps the safety boundary local: ordinary recordings require
  visible user selection and/or confirmation. Optional one-shot and daily/weekly
  unattended plans each require a fresh local fixed-region selection and bounded
  lease approval before later runs.

## Release And Scope

The v0.1.23 release is a Windows x64 self-contained portable package.
For ordinary 1–30 second recordings, the package CLI can start Recorder,
submit one quick API request, and wait for a verified MP4 in a single call.
The user still selects the region when needed and approves recording locally.
Longer and advanced recordings continue to use the existing HTTP API.
Set the extracted folder as the agent's working directory and say "Record a
selected region for 10 seconds." See [Quick Start](QUICKSTART.md) for details.

Existing capabilities include durable
ordinary recording history, unified indexed history, on-demand health and
ordinary, plan and future-window artifact metadata checks, explicit ordinary
main-video and five-attachment content verification, read-only retention
previews and durable plan and future-window publication identity. All new video recordings deliver video only. Ordinary raw/quick requests may omit artifacts or use an empty object or `artifacts.bundle=false`; explicit true is rejected before recording setup. Existing Bundle history remains readable and protected. Eligible completed MP4-only recordings in the host's Videos directory
can be submitted by ID for separate local deletion confirmation. Explicit-ID
read-only evaluation accepts up to 20 recordings. Ordered cleanup requests
accept up to five recordings, with a separate local approval for every file;
refusal or uncertainty stops the remaining items. They are not atomic and
never resume deletion after restart. Cancellation
preserves the file; retries with the same key return the original result.
Existing Bundle outputs remain protected. The optional quota checks recorded
logical bytes and estimated output size; it is not a hard disk quota and does
not delete files.
Inspect `/capabilities` for the running host's
actual scope and use [the API reference](docs/api.md) for contracts.

When upgrading an existing installation, back up its data directory first.
The database remains at SQLite schema V51. If upgrading from a pre-V51 build,
keep migrated data with V51-compatible binaries.

## Recording Audio

**Recordings are silent by default.** Asking to record an entire PotPlayer
window does not enable audio: "entire window" describes the picture area.
Request audio explicitly in natural language.

| Mode | What is captured | Current scope |
| --- | --- | --- |
| No audio (default) | Video without an audio track | Ordinary window, region and display video |
| System audio | All sound on the selected speaker/headphone output | Ordinary window, region and display video |
| Microphone | The selected microphone input | Ordinary region and display video; not window-content recording |
| System audio + microphone | A mixture of both | Simultaneous capture is not supported |

Example requests:

```text
Record the entire PotPlayer window for 20 seconds with system audio, without the microphone.
Record a selected region for 15 seconds using the AirPods microphone, without system audio.
Record the primary display for 15 seconds without any audio.
```

System audio is not isolated to the recorded window: other applications using
the same output device may be heard. Without a device selection, system audio
uses the current Windows multimedia default output. Microphone selection uses
a uniquely determined default or single active input; the agent must resolve
ambiguous choices. The device approved locally stays fixed for that recording;
changing the Windows default does not switch capture to another device.

Check the audio mode and device in the local confirmation dialog. If requested
audio is unavailable, the agent must explain the failure rather than substitute
a silent recording or enable a microphone without permission. Executing a raw
fixed-region profile retains its complete silent specification. Ordinary quick
recording can instead reuse only its region via `target.type=profile_region`,
with this request's duration and system audio or microphone; the stored profile
is unchanged. Fixed-region unattended plans and screenshot sequences remain
silent. Inspect live capabilities before recording.

## Recommended Agent Path

For ordinary 1–30 second videos, use the package's
[`record-quick` single call](QUICKSTART.md#short-recordings-in-one-invocation).
Do not start Recorder or read its API key separately first.

For longer and advanced workflows, or an older package without `record-quick`:

1. Start or reuse the app:

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe ensure-running --json
```

2. Read the API key from the returned `api_key_file`.
3. Use `POST /api/v1/recordings/quick?wait_for=recording&wait_ms=25000` for common intents:
   - `primary_display`
   - `windows_display` (use the positive number shown by Windows “Identify”)
   - `active_window`
   - `selected_region`
   - `last_region`
4. Treat `wait.goal_reached=true` as the trusted start signal. On timeout,
   continue with recording long-polling; then report the final MP4 path.

For optional environment diagnosis, agents read authenticated `GET /api/v1/health`
directly. Task status and results use recordings/history/plans APIs; a health
snapshot does not authorize capture or automatic retries. See
[the health contract](docs/api.md#on-demand-read-only-health).

Advanced agents may still use the lower-level API for precise display, window,
region, output, or nested-recording control.

The bounded creation wait observes local approval, preparation, countdown, and
the trusted first frame without approving anything over HTTP. Omitting the
query preserves the immediate creation response for compatibility.

For “record Windows display 2”, prefer one quick request:
`{"target":{"type":"windows_display","windows_display_number":2},"duration_seconds":30}`.
The number is the positive integer shown by Windows “Identify”, not the API
ID suffix. The current snapshot must contain exactly one reliable match;
otherwise refresh capabilities/displays or ask the user to disambiguate.
The request still requires local confirmation. The raw API remains available
for precise control and uses the opaque `id` from `/displays` unchanged as
`source.display_id`.

## Runtime Data

The per-user installer targets Windows x64 and fixes program files at
`%LOCALAPPDATA%\Programs\AgentRecorder`. Installation finishes without launching
the App or enabling autostart, a desktop broker, plans or recording. The agent
can read current-user `HKCU\Software\AgentRecorder\Installation` values
`InstallLocation`, `CliPath`, and `AgentInstructions`, then run the discovered
CLI's `ensure-running --json` through its authorized user-desktop execution
channel. Registry discovery is not permission or a cross-account launcher.
The Start menu entry opens the existing tray App.

Installed App, CLI (including shutdown and interactive-launch status), and
Start menu use `%LOCALAPPDATA%\AgentRecorder` by default. Explicit data-directory
selection has priority over `AGENT_RECORDER_DATA_DIR`, then this default;
installation markers are validated before overrides. Data cannot overlap the
program tree or pass through reparse points. Portable data/keys are never copied
or merged. A corrupt, unsupported or conflicting marker fails explicitly.

Before upgrade, back up the data directory. Ask the agent to use the original
matching CLI's authenticated, idle-only `shutdown --json` and confirm exit.
An active or unknown instance, occupied API port, or unresolved product
autostart/broker integration blocks upgrade/uninstall, including silent mode;
force-close options are rejected. Integrations require explicit ownership
review using the existing commands; the installer removes no unknown tasks.
Uninstall removes only logged program files and owned entries. Videos, database,
key, settings, audit and extra files placed in the program directory remain;
there is no erase-data option. Cancellation/failure may leave a partial program
installation: do not downgrade binaries against upgraded data. Only normal App
startup opens/migrates the database. Unsigned means publisher identity has not
been verified; do not disable SmartScreen or antivirus.

For the portable package, `AgentRecorder.Cli.exe ensure-running` defaults the
data directory to `<package-root>\.local-data` and passes that path to the app
through `AGENT_RECORDER_DATA_DIR`.

If `AgentRecorder.App.exe` or `AgentRecorder.Headless.exe` is launched directly
without `AGENT_RECORDER_DATA_DIR`, the default data directory is
`%LOCALAPPDATA%\AgentRecorder`.

Agents should use the paths returned by `ensure-running` or
`GET /api/v1/capabilities`, especially `data_dir`, `ready_file`, and
`api_key_file`.

For tray, selection, and approval UI, the agent should invoke the selected CLI command
through its host's approved command-execution channel for the current user's
interactive desktop. Starting a process on an isolated desktop is not proof
that its dialogs are visible. Recorder does not escape a sandbox or dispatch
across Windows accounts; without an appropriate execution channel, fail closed.
`host.supports_region_selection_ui` describes host capability, not visibility.

## Capabilities

- Quick intent API for primary display, a Windows display number, active window, and selected region.
- Optional bounded creation wait that lets one raw or quick POST observe local
  approval, preparation, countdown, and the trusted first frame without
  weakening local confirmation.
- Lower-level display, window, and region recording APIs.
- New ordinary raw window video and quick `active_window` default to the
  selected window's own content, excluding obscuring windows. The raw field
  `required_capture_semantics: "window_surface"` remains optional and explicit;
  omitting it uses the same strict contract. WGC unavailable or unsupported
  combinations fail explicitly, without a desktop fallback or experiment flag.
  Confirmation/preview and the approved backend share this meaning. The raw
  default passed a 15-second occlusion test; quick has automated coverage but
  still awaits its dedicated desktop test.
- Ordinary window video fixes a `window_id`. This public strict path uses
  WGC to capture the selected window even while another window obscures it,
  supports no audio or an explicitly approved system-loopback endpoint, and
  accepts a bounded 1-1800 second duration. It never falls back to a desktop
  rectangle. A 300-second PotPlayer video-and-system-audio run passed local
  desktop acceptance. The separately authorized future-window flow also passed
  a supervised 30-minute run on the current hardware; this is not a guarantee
  for every device or a full livestream.
  Microphone and manual/unbounded window video are rejected. Independent silent
  windows use separate ordinary raw requests with distinct window IDs and no
  nested/parent/session fields. Each has its own local approval, countdown,
  output and stop action. The default budget is two, configurable at host
  startup from 1 through 10; unretired delivery work still holds its slot.
  Controls show WIN1/WIN2 and subsequent process-local numbers with target titles.
  Three windows passed 180/90/30-second desktop acceptance. The software limit
  is not a ten-window hardware or long-run stability guarantee. Quick, Profile,
  audio, display/region and unattended flows do not join this parallel contract;
  the older window outer/inner contract remains separate and compatible. Window
  title bars/controls are included; the player area is not cropped automatically.
  System sound covers the approved output endpoint, not only that window.
  Independent window content is unaffected by overlap. Region OUTER/INNER captures
  actual desktop rectangles and retains its layered controls. Neither mode
  automatically composites a picture-in-picture video.
  Desktop rectangles use region/selected_region/last_region. Window screenshot
  series remains a composed screen rectangle; frozen legacy history is unchanged.
- One-shot future-window authorization binds an exact local executable, a new
  post-approval process and its unique eligible window, a frozen output target,
  and either no audio or a fixed system-loopback endpoint. A locally approved,
  revocable grant lasts at most one hour and permits one run of up to 30 minutes.
  The external agent opens the player and resolves the window; Recorder does
  not recognize livestreams or control playback.
- Reusable fixed-region profiles: save a fresh local selection, manage immutable
  versions through authenticated APIs with ETag/If-Match, and request an ordinary
  recording using an exact profile reference. Reuse still requires local approval
  and unchanged display geometry. Raw `profile_ref` executes the full silent
  specification. Quick `profile_region` reuses only the region, with a per-request
  1-600 second duration and optional system audio or microphone. The stored
  profile is unchanged; configuration is not capture authorization or a plan.
- Interactive selected-region UI with precise coordinates, size presets, edge/window
  snapping, click-to-pick visible windows, and resilient top-most behavior across
  multi-monitor desktops.
- Mandatory local user confirmation before recording starts, with target preview,
  an optional local save-directory choice, and a no-capture 3-2-1 countdown on
  microphone, ordinary no-audio FFmpeg, and deferred-start WGC paths. The
  countdown is configurable per recording from 0 to 10 seconds.
- Before-confirmation and before-start preflight checks for output paths, free
  space, capture bounds, encoders, and source availability.
- A click-through red recording border and timer around the captured region,
  with restrained REC motion that follows the Windows animation preference.
- A stable rounded-capsule stop control next to each active recording; clicking
  it stops only that recording.
- A global stop hotkey (`Ctrl+Shift+F10`) that stops all active recordings.
- New recordings do not accept chapter marks. Existing historical marks remain readable.
- Tray menu stop entry that adapts to one or multiple active recordings.
- Chinese and English local UI with DPI-aware confirmation, selection, recording
  indicators, and stop controls.
- A DPI-aware region selector with corner accents and a live
  `W x H @ display` label.
- HTTP self-approval blocked with `405 METHOD_NOT_ALLOWED`.
- Optional one-shot unattended fixed-region plans. This mode is disabled by
  default and limited to one silent run, a lease of at most one hour, a run of
  at most ten minutes, natural wake only, and an interactive desktop. The agent
  cannot approve or widen the lease over HTTP.
- Optional daily/weekly fixed-region plans, also disabled by default. Each
  plan has a bounded date range, run count, total duration, and lease validity.
  The user selects the fixed region and approves the lease locally; each due
  occurrence revalidates the approved environment before one silent run.
  Missed windows and uncertain starts are not retried or silently replaced.
- One-time `required` plans do not use a Lease. The user selects a fixed region
  and approves plan creation locally, then must approve a second local dialog
  when that run is due. If that execution dialog is rejected, expires, or
  cannot be shown safely, no recording starts. Agents can query durable plan
  and run status with `GET /api/v1/plans/{plan_id}/status`.
- Nested recording: one outer recording can capture the process of starting an
  inner recording.
- On the validated default FFmpeg path, role-aware capture visibility keeps
  ordinary recording controls out of the video while allowing a safe inner
  recording UI to remain visible to its outer recording. Capture exclusion
  remains best-effort across Windows and DWM variants.
- User-level autostart controls and FFmpeg prewarm support.
- A bounded local `perf_summary` with cold/warm P50/P95 diagnostics.
- Historical Bundle attachment identity, hash and retention protection remain supported. New videos do not generate attachment packages.
- Microphone recording through an isolated Windows WASAPI helper, followed by
  AAC muxing, with device enumeration, continuity diagnostics, runtime recovery,
  and stable failure reporting. Bluetooth Hands-Free inputs can automatically
  pair their matching render endpoint and hold the duplex HFP link; AirPods Pro
  and Focal Bathys have passed supervised product-path recording. Compatibility
  is still device/driver dependent. FFmpeg dshow remains an explicit diagnostic
  fallback.
- System-audio recording records a selected Windows render endpoint
  through isolated WASAPI loopback capture, synchronizes it with FFmpeg video,
  muxes AAC into the final MP4, and exposes continuity diagnostics without
  misreporting system audio as a microphone. The locally approved endpoint stays
  fixed for the recording; changing the Windows default output does not silently
  change what is captured, and returning to the approved endpoint uses bounded
  same-endpoint recovery. The complete region-selection,
  save-path confirmation, countdown, REC indicator, recording, and finalization
  flow has passed a supervised 12-second real-device A/V acceptance test. This
  is a public capability: `/capabilities` and `/permissions` report
  `system_audio.supported: true` with fresh `ready`/`no_devices`/`unavailable`
  status, and `/audio/devices.output_devices` exposes safe active render
  endpoint IDs for explicit selection.
- The experimental native WGC continuous pipeline supports eligible 1-60 second,
  no-audio display, window, and selected-region targets behind separate local
  environment switches. It provides
  non-capturing availability probes, short-lived success caching, authenticated
  first-frame evidence, explicit window lifecycle failures, stable display
  identity and topology revalidation, runtime display-loss failure, GPU region
  cropping, bounded duration, and automatic FFmpeg fallback. Display stability,
  disconnect handling, occluded-window capture, and 60-second recording have
  passed supervised desktop acceptance on the current hardware. The self-contained portable package includes exactly one
  production `AgentRecorder.WgcHelper\wgc-native-helper.exe`. The pipeline
  remains disabled by default for those general targets. The separate strict
  fixed-window request above is exposed through the public raw API when its
  non-capturing capability probe succeeds.
- Local audit log and MP4 output.

### Per-recording pre-capture countdown

Both raw and quick recording requests accept the optional top-level
`countdown_seconds` field. It defaults to `3`, accepts integer values from `0`
through `10`, and rejects other JSON types or out-of-range values with
`400 INVALID_ARGUMENT`. `0` keeps preparation, confirmation, preflight, and
first-frame safety boundaries but skips the visible countdown. The normalized
value appears in the confirmation summary and recording status/configuration.

### Bounded screenshot series

Raw and quick recording requests can set `mode: "screenshot_series"` with an
integer `interval_ms` from `1000` to `3600000` and exactly one finite bound:
`max_count` (`1..300`) or `max_duration_seconds` (`1..86400`). Duration plans
use `ceil(duration_seconds * 1000 / interval_ms)` points and are rejected when
the plan exceeds 300 frames. Audio is not supported and is rejected before
target or device resolution.

Each point runs one single-frame PNG capture. After local confirmation and the
normal preflight/countdown, frames are staged under the data directory's
recording-specific `temp` area, validated, atomically named, and published as a
directory containing `frame-0001.png` and `series.json`. A completed or partial
cancelled series is reported through `mode`, `series`, and `output`; marks and
the MP4 bundle are not applicable. The indicator uses `SHOT x/y` (or Chinese
`截图 x/y`) rather than `REC`. Each point claims its scheduled start before
launching one finite FFmpeg process; the input cadence is a bounded low-latency
gdigrab setting and `-frames:v 1` still limits the process to one PNG. Claims
are serialized, so the scheduler does not launch parallel catch-up frames. The
first valid PNG submission is the timeline anchor (`t=0`). In `series.json`,
`captured_offset_ms` is the valid-submit offset, `lateness_ms` is non-negative
claim lateness excluding the frame's own capture/encode time, and
`capture_duration_ms` is the monotonic claim-to-valid-submit duration. These
fields describe timing truthfully; they are not a fixed desktop latency or
real-time guarantee.

## Documentation

- [Quick start](QUICKSTART.md) ([简体中文](QUICKSTART.zh-CN.md)): getting started with a local AI agent.
- [Agent instructions](AGENT-INSTRUCTIONS.zh-CN.md): startup, API workflow and safety (Chinese).
- [API reference](docs/api.md): contracts and limits; [Chinese reference](AGENT-API-REFERENCE.zh-CN.md).
- [Safety](docs/safety.md): authorization and evidence boundaries.

## Project Layout

```text
src/
  AgentRecorder.App            WinForms tray host, selection UI, confirmation UI
  AgentRecorder.Api            localhost HTTP API
  AgentRecorder.Core           recording state machine and contracts
  AgentRecorder.Capture        FFmpeg/WASAPI capture, muxing, legacy Bundle compatibility, prewarm
  AgentRecorder.Windows        Win32 display/window helpers
  AgentRecorder.Security       safety policy checks
  AgentRecorder.Logging        audit log writer
tools/
  AgentRecorder.Cli            agent startup/autostart helper
  AgentRecorder.AudioHelper    isolated Windows WASAPI microphone helper
  ffmpeg/bin                   bundled FFmpeg/ffprobe
  wgc-native-helper            experimental native WGC helper source and build
tests/
  AgentRecorder.Tests
  AgentRecorder.AudioHelper.Tests
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


## Agent-requested idle shutdown

When the user asks to exit Recorder, the agent runs:

```powershell
AgentRecorder.Cli\AgentRecorder.Cli.exe shutdown --json --data-dir "<data_dir returned by ensure-running>"
```

Check `capabilities.host_shutdown.supported` first. This build supports authenticated idle-only `POST /api/v1/host/shutdown`, with no force option. The CLI never starts an app or accepts a PID; it verifies ready/capabilities, user, executable, process start identity and instance nonce, sends the existing key, and waits up to 15000 ms (`--timeout-ms`, 100–120000) for that original process to exit. `exited` is confirmed process termination; `not_running` means no ready file and no held host instance. `busy`, `unsupported`, `timeout` and `unknown` are not successful exits. A 202 API `accepted` response alone does not mean exited.

Active confirmation/selection/authorization, preparation/countdown/recording/finalization, deletion and unretired or unknown resources return 409 `HOST_BUSY` with a stable `error.details.reason_code`; service admission stays open. To stop an active recording, use its existing stop API only when authorized, wait for resources, then request exit. Exiting Recorder does not authorize stopping recordings, revoking saved plans/grants, restarting or replaying work. No process killing or tray automation is part of this command. After a confirmed exit, a separately requested restart uses the usual `ensure-running --json --app "<desired build>" --data-dir "<same directory>"`.

The tray App host supports this exit operation. Other hosts, including Headless, advertise `supported=false`; the CLI returns `unsupported` without falling back to an unauthenticated event, process kill or UI automation.


### Saved Regions

Reuse a saved region with a new duration and audio choice, without changing the saved configuration. Each recording still needs local confirmation. Audio defaults to off and countdown to 3 seconds. See [saved-region usage](QUICKSTART.md#saved-regions) and the [API contract](docs/api.md#quick-recording-from-a-saved-region-profile_region).


### Independent window recording

Independent silent windows have both a count and a pixel-rate limit. See the [startup budget](docs/api.md#independent-window-startup-budget) for settings and API errors. Neither limit guarantees performance on a particular machine.
