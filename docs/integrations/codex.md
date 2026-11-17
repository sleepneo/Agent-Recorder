# Recorder local plugin bootstrap

Plugin **0.3.2** provides connection, explicit tray-host startup and short silent
selected-region recording on Windows
x64. It requires a separately obtained **self-contained portable Recorder
0.1.25**, with the official split `AgentRecorder.App/` and
`AgentRecorder.Cli/` layout and their runtime dependencies. Framework-dependent,
flat-layout, installed and other-version packages are rejected. Metadata checks
verify layout and version consistency; they do not authenticate a publisher.
Only 1..30 whole-second selected-region clips are supported, default 10 seconds,
with a three-second countdown and MP4-only output. There are no sound, window,
display, last-region, plan, global stop, deletion, shutdown or automatic-startup tools/hooks.
Local selection and recording consent remain product requirements.
Platform identity and API keys do not grant recording approval or server-side
isolation between agent platforms.

## Tools and binding

All six inputs are JSON objects with `additionalProperties=false`.

| Tool | Inputs | Effect |
| --- | --- | --- |
| `recorder_binding_get` | `{}` | Read-only binding validation; never creates directories or starts Recorder. |
| `recorder_binding_set` | Required string `package_root`, string `data_dir`; optional boolean `initialize_new_data_directory=false` | Validate explicit absolute local paths and save the binding. True requires a genuinely new directory and explicit user intent. No host launch or business DB creation. |
| `recorder_host_ensure` | `{}` | Explicitly start/reuse the bound App through its CLI in tray mode. Fixed CLI wait 10 seconds, observation budget 15 seconds. |
| `recorder_record_region` | Required lowercase canonical nonzero UUID `operation_id`; optional integer `duration_seconds=10`, range 1..30 | Persist bounded local admission and observe one quick selected-region request asynchronously. Local selection/approval remains necessary. |
| `recorder_operation_get` | Required same `operation_id` | Observe that operation and query only its mapped native recording and output; never starts a host or creates a selection. |
| `recorder_recording_stop` | Required original `operation_id` and exact mapped native `recording_id` (`rec_` + 12 lowercase hex) | On explicit user stop intent only, verify the same binding/host generation and stop that exact recording once; never starts a host. |

The three bootstrap tools retain their structured content: `ok`, `status`, `reason_code`, and nullable
`binding`, `host`, `product_error_code`, `product_error_source`. Statuses are
`unconfigured`, `valid`, `invalid`, `ready` and `unknown`. Product errors retain
their CLI source and code; raw stdout/stderr and exception bodies are withheld.
`ready` means the CLI verified identity, readiness capabilities and interactive
desktop proof. `human_window_visibility=not_verified` and `capture=not_checked`
remain explicit. Product version `0.1.25` is checked separately from the CLI's
HTTP readiness protocol identifier `v1`; neither is the MCP protocol version,
which is negotiated by the official SDK.

The shared per-user binding is
`%LOCALAPPDATA%\AgentRecorderIntegrations\binding.json`, independent of CWD,
the plugin cache and plugin version. Missing reads are side-effect free.
Process option `--binding-file <absolute-local-path>` is for isolated development,
not a tool argument. The schema stores version 1, normalized program/data paths,
verified product version, initialization policy (`existing`/`initialize_new`),
initialization state (`pending`/`initialized`/`unknown`) and host ensure state
(`idle`/`unknown`). It contains no credentials or recording details.
Windows file identities anchor the prepared data directory and initialized DB;
replacement at the same path fails and requires an explicit validated rebind.
The binding parent must be outside both the selected data and package roots,
including equality and case-insensitive descendants. Conflicting overrides fail
with `binding_location_conflict` before creating any directory, lock or temporary
file. The default is a sibling of the usual `AgentRecorder` data root. Old default
locations are not discovered, migrated, overwritten or deleted automatically.

## Verified upgrade scope

Isolated SDK/stdIO regressions move between independent plugin directories and
unrelated process working directories while retaining the same absolute external
package/data binding, directory/database file identities, operation IDs and stop
intent/receipt markers. They cover completed output, unknown submission without a
native ID, and known or uncertain stop receipts. Reads and replays do not create
another recording or stop. Frozen schema-1 binding/operation records, including
null confirmation IDs, remain readable; legitimate status cache updates are allowed.

An explicit test-only probe checks the original 0.2.1 writer with the retained
0.3.0 reader and the separately hashed 0.3.1/0.3.2 patch readers, and
uses private copies for an old-writer/new-reader comparison. Normal source tests
do not depend on retained historical packages. This verifies that specific forward
upgrade with Recorder **0.1.25**, not downgrade, all future Recorder versions, other
SDKs/platforms, or actual Codex installation/reload behavior. Program metadata,
capability and data-identity failures remain fail-closed without switching paths
or silently creating a database. Metadata checks do not authenticate arbitrary
same-version replacement binaries. Deliberately deleted media cannot be promised
again, and a restarted/changed host cannot impersonate the original host generation.

The stable binding and evidence are outside versioned install caches. Cache reload,
MCP EOF and a complete desktop-client exit are distinct: normal EOF tests do not
guarantee a real App survives the client's outer process/Job retirement.

Writes use an exclusive cross-process sidecar lock (1.5-second contention
budget), a flushed same-directory temporary file and atomic replacement. Failed
validation preserves the previous binding. Corrupt or unsupported schema files
are not replaced. UNC, mapped network, relative, device, traversal, alternate
stream and reparse paths are rejected. Existing data requires a nonempty key
file and the original SQLite-format business file; this observes metadata and
the file header only, and does not perform SQL validation, migrations or repair.
Recorder remains authoritative for the database. Lost/invalid initialized data
fails before startup and is never silently recreated.

New initialization prepares an empty directory only. Before CLI submission the
adapter durably marks startup uncertain, and pending initialization unknown.
Only a verified ready result with initialized files clears these states. Crash,
timeout, cancellation, malformed output or product refusal leaves uncertainty
durable. Do not automatically retry, reset the binding or switch packages. After
resolving the original attempt, an explicit rebind can adopt verified existing
data; it cannot recreate a lost directory with the new-directory flag.
Persisted `initialization_unknown` and `host_start_unknown` remain `ok=false`,
`status=unknown` on both subsequent binding reads and ensure calls, without
clearing the binding or submitting CLI again. Deterministic invalid paths,
versions and schemas still return `invalid`; missing bindings are `unconfigured`.

The startup adapter uses `ProcessStartInfo.ArgumentList`, closes CLI stdin, and drains
stdout/stderr concurrently with a 65,536-character limit per stream. CLI output
is parsed and reduced to an allowlisted result. Recording adds an identity-bound
HTTP layer; it reads the fixed bound key file into local memory only, never into
tool parameters, results, operation records or logs. No model-supplied credentials,
URLs, ports or arbitrary routes are accepted.
EOF, timeout and cancellation end observation; they do not issue Stop, revoke
authorization, shut down a reused host or kill any process tree. No adapter Job
is created. A parent desktop client's inherited Job/restrictions still apply;
synthetic process tests cannot establish that client's real desktop behavior.
The isolated lifecycle regressions distinguish normal CLI exit, disposal of the
SDK client while caller-owned streams remain open, explicit stdin EOF, and closure
of a dedicated synthetic Job. None is a complete desktop-client exit experiment
or proof of the cause of an earlier real App exit. No automatic breakaway or
independent-launch workaround is installed.

## Recording operations and failure boundaries

`operation_id` is generated before submission; it is not `recording_id`. The
submission tool returns local `accepted` promptly, independently of the 15-second
host readiness budget. A worker then verifies host identity and durably writes
`submitting` intent before its one POST to `/api/v1/recordings/quick`. The fixed
body selects a region with 120-second selection timeout, disables microphone and
system audio, requests the chosen duration and three-second countdown, and leaves
output/artifacts at product defaults. No blanket record-quick subprocess wait is used.

Minimal schema-1 association records live beside the binding in
`<binding-file>.operations/`: canonical request, binding/file identities, worker
generation, host instance, native IDs and last machine state. Files use a SHA256
corruption checksum, flushed same-directory atomic replacement and a cross-process
gate with 1.5-second contention budget. Persistent worker markers also prevent a
missing operation file from being interpreted as a fresh admission. No SQL,
business-history/media copy, queue recovery or background cleanup is added.
At most 128 admissions and one unresolved operation are allowed; unknown holds
that admission. Four concurrent product queries per MCP process are allowed.
Cancelled admission, corrupt/unknown records, capacity, contention and uncertain
writes fail closed. There is no automatic reset/retention tool.

Same UUID plus normalized request/binding replays as observation; a different
duration or binding identity conflicts. Unknown/not_found is not permission to
resubmit or generate another UUID. An abandoned accepted/submitting record is
projected unknown when its worker is gone and is never resumed as a POST. Native
IDs come only from complete product responses; a known ID is retained and only
that recording is queried across MCP restarts. Selection cancelled/timed out,
approval rejected/expired and failures never cause a second selection request.

The transport follows the existing CLI QuickHost/QuickLoopback protections:
active-user desktop, bound ready/key paths, held process generation/image/SID,
instance and capabilities, plus Windows TCP peer ownership **before** any HTTP
request/key bytes. Proxy, redirects, cookies and submission reconnect are disabled.
Connect budget is three seconds; capability/ordinary-read budget five seconds,
query total eight seconds, selection POST observation 130 seconds and total worker
budget 155 seconds. Responses are limited to 64 KiB with 16 KiB headers. The
binding lease is held during submission observation to prevent cooperative rebind.

Operation results include `ok`, `status`, `reason_code`, `operation_id`, nullable
`recording_id`, `confirmation_id`, product status/error code and nullable output
path/size/duration. Local accepted/submitting do not prove UI or capture. Product
pending confirmation, preparing/countdown, recording, finalizing and terminal
states are distinct. Completed success requires matching-ID completed and /output
evidence, matching path/positive size, present MP4/H.264 video, no audio stream,
positive geometry/fps and plausible duration. It reads no video content. Insufficient
output evidence remains unknown with the original operation and native ID. Do not
scan output directories, read SQLite or invoke a separate recording script as a
normal plugin fallback. Disconnect/cancel/EOF
only ends observation, never Stop/Reject/revoke/shutdown; residual dialogs or capture
may remain and must not be reported as cleaned up.

## Build and install for local testing

Developers need .NET 8 SDK and PowerShell 7.2+. The adapter pins the official
[C# SDK Core 2.4.0](https://www.nuget.org/packages/ModelContextProtocol.Core/2.4.0),
which includes a .NET 8 target. It adds no Node/Python runtime or Core/DB project
dependency. End users run the self-contained executable without a .NET SDK.

From the repository root:

```powershell
pwsh -NoProfile -File scripts/build-codex-plugin.ps1
dotnet test tests/AgentRecorder.Mcp.Tests/AgentRecorder.Mcp.Tests.csproj
```

The builder requires a new output directory within its private workspace build
scope and prints the absolute development path. Use its default or supply
`-OutputDirectory` within that scope. The recording candidate default is the fixed
private `.local-data/task432/development-stop` directory; arbitrary additional absolute roots are
not permitted. It publishes the self-contained plugin, validates
canonical schemas/references/inventory and runs an EOF executable smoke with a
unique private binding override. It generates a local marketplace sample next
to the plugin. It does not install, enable or register anything. Tests create
unique synthetic configurations and processes under the same workspace scope,
always passing `--binding-file` explicitly, and normally delete their fixtures.
The semantic gate additionally requires the bundled command
`./server/AgentRecorder.Mcp.exe`, resolves it from the plugin root and rejects
traversal, absolute paths and reparse paths. It does not search PATH or expand
variables in command. `cwd` is checked separately and remains `${PLUGIN_ROOT}`.
Schema validation alone cannot establish these runtime semantics.

For desktop testing, review the generated directory. Preserve existing
marketplace entries. Per the [official local marketplace workflow](https://developers.openai.com/plugins/build/plugins),
register the generated marketplace root with `codex plugin marketplace add "<absolute-development-root>"`.
Clients whose installed CLI exposes `codex plugin add` can then install with
`codex plugin add agent-recorder-bootstrap@agent-recorder-local-dev` and verify
with `codex plugin list --marketplace agent-recorder-local-dev --json`.
Otherwise, select the local source in Plugins and explicitly install it.
Installation/enabled state alone does not prove MCP discovery. Select the plugin
in the target chat and verify tool discovery before invoking it. If tools are
absent, check startup/configuration diagnostics; repeated mentions or a direct
terminal launch do not prove that the plugin entry works. Do not copy files directly
into a version cache. The current
[portable manifest and stdio specification](https://developers.openai.com/plugins/deploy/desktop-only)
uses root `plugin.json`, `mcp.json` and `skills/`. Per the
[Agent Plugins stdio runtime rules](https://agent-plugins.org/client-implementers/mcp-runtime),
the native executable command uses a contained `./` path; `${PLUGIN_ROOT}` is
expanded in cwd, not command. No legacy
overlay is shipped; actual tool discovery and any older-client compatibility
must be verified in the target desktop client. This is a local development
package, not a published directory listing or account-uploaded plugin.

In a new unrelated-project chat, ask the agent to check the binding, provide
the intended external package/data paths, then explicitly request host startup.
Check that the selected App is visible and the host remains after disconnect.
Desktop installation, visible App startup and recording are separate validation
steps; synthetic development tests do not establish them. After installation is
independently verified, use a natural-language silent region request, select and
approve locally, and have the agent query its original operation until the MP4
result is confirmed.

## Explicit stop and short-clip results

Only explicit user stop intent permits `recorder_recording_stop`. It requires
both the original operation UUID and exactly its mapped native recording ID,
with the same binding and pinned host generation. It never ensures a host,
enumerates tasks, guesses latest, approves/rejects consent or invokes global stop.
Its fixed request is POST `/api/v1/recordings/{id}/stop` with
`{"reason":"user_requested"}` through the same peer-verified transport.

An authenticated bounded GET first verifies this exact recording. A terminal
target returns `already_terminal`, without POST or attributing its completion
to this stop. Stopping/finalizing targets are observed without another stop.
Native pending confirmation/preparing/countdown stop can cancel with no video.
The tool reports `not_sent`, `accepted`, `rejected`, `unknown` or
`already_terminal`, retaining both IDs and observed product state/reason.
An acknowledgement does not prove saved output. Continue `recorder_operation_get`
for actual terminal/output evidence; cancellation/EOF/ending observation is not
stop intent and never stops the host.

Stop evidence uses a separate schema under `<binding-file>.operations/stops/`,
leaving the original schema-1 operation fields and checksum input unchanged.
At most 128 stop records, 16 KiB each, are allowed; each has a flushed CreateNew
intent marker plus checksummed atomic intent/receipt. The existing 1.5-second
cross-process gate protects re-read/validation/intent and receipt updates, but is
released before HTTP. Operation cache writes cannot erase stop evidence. Missing
or corrupt evidence fails closed; restart, concurrent or repeated calls never
recover/retry POST. No cleaner/reset tool is added.

Each stop call has a nine-second total observation budget, including peer/capability
checks, GET, locks and a separate five-second maximum stop POST. Four concurrent
stop observations per process are allowed independently of the four query slots,
so waiting on stop does not consume query capacity. Existing response limits,
no proxy/redirect/reconnect and binding protection remain. This is narrow adapter
target validation, not server-side isolation between platforms sharing a key.

Only the trusted same-ID completed status's native `stop_reason` of
`user_requested`, `floating_button`, `tray_menu` or `global_hotkey` permits a shorter
positive clip. Local stop intent or POST acknowledgement does not. The duration
upper bound remains planned seconds plus max(1.5 seconds, 10%); natural completion
retains its original closeness threshold. All matching-ID/path/positive-byte,
MP4/H.264, geometry/fps, no-audio and probe requirements remain unchanged.
