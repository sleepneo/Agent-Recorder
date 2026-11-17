# Codex Plugin Preview

Plugin **0.3.3** is a local Windows x64 stdio MCP adapter and bundled Skill.
It requires the separate **Recorder 0.1.25 self-contained portable package**.
Recorder remains the recording, consent and history authority.

| Component | Distribution | Scope |
| --- | --- | --- |
| Recorder 0.1.25 | [Stable portable release](https://github.com/sleepneo/Agent-Recorder/releases/tag/v0.1.25) | Existing full CLI/HTTP capabilities |
| Codex plugin 0.3.3 | [GitHub prerelease](https://github.com/sleepneo/Agent-Recorder/releases/tag/codex-plugin-v0.3.3) | Silent 1-30 second selected-region video, query, exact-ID stop |

The plugin does not contain Recorder. This GitHub preview is not an OpenAI
plugin-directory submission, review approval or default recommended entry.
Audio, window/display targets, plans, global stop, deletion and shutdown are not
exposed; use the existing portable CLI/API for those capabilities.

## Install And Use

1. Download both ZIPs and verify their release SHA-256 files. Extract them into
   separate durable locations. Do not store recordings in a plugin version cache.
2. Register the extracted marketplace root (containing `.agents` and
   `agent-recorder-bootstrap`) through the supported local workflow:

   ```text
   codex plugin marketplace add "<absolute-extracted-marketplace-root>"
   codex plugin add agent-recorder-bootstrap@agent-recorder-local-dev
   codex plugin list --marketplace agent-recorder-local-dev --json
   ```

   Existing development installations use the same marketplace name. Inspect the
   configured source before replacing only that source, preserve other entries
   and never edit the installed cache by hand.
3. Reload the client when required and select Agent Recorder Bootstrap in a chat.
   Ask the agent to check the binding, then provide the explicit Recorder package
   and data paths. Existing data is reused by default; a new empty data directory
   requires explicit user intent. No API key needs to be supplied to the agent.
4. Say **"选区录屏 10 秒，不录声音。"** The agent submits once; select and approve
   locally, observe the three-second countdown, then receive the verified MP4.
   The current project need not be the Recorder directory.
5. To stop early, explicitly ask to stop that recording. The agent uses its
   original operation UUID and mapped native recording ID, never "latest".

The agent is expected to have local file, process execution and desktop access.
Recorder still enforces the active user/desktop and local approval. Connection
permission or a shared API key does not isolate platforms or authorize capture.

## Tools And Binding

All inputs reject additional properties and arbitrary URLs, ports, credentials,
commands, output paths or approval flags.

| Tool | Inputs | Effect |
| --- | --- | --- |
| `recorder_binding_get` | `{}` | Validate the saved binding; no write or launch |
| `recorder_binding_set` | `package_root`, `data_dir`; optional `initialize_new_data_directory=false` | Explicit absolute local binding; true prepares only a genuinely new empty directory |
| `recorder_host_ensure` | `{}` | Explicit bound CLI tray startup/reuse, not recording consent |
| `recorder_record_region` | Canonical nonzero lowercase UUID `operation_id`; integer `duration_seconds=10`, 1-30 | Admit one silent selected-region request with local selection/approval |
| `recorder_operation_get` | Original `operation_id` | Query that operation and its mapped native recording/output |
| `recorder_recording_stop` | Original `operation_id` and exact `recording_id` | Explicitly stop only that mapped recording, at most once |

The stable per-user binding is
`%LOCALAPPDATA%\AgentRecorderIntegrations\binding.json`, outside the selected
package/data roots and independent of CWD, plugin cache and version. Schema 1
stores normalized paths, product version, initialization/ensure state and Windows
directory/DB identities, not credentials. Lost, replaced or incompatible paths
fail closed, without silently creating data, switching hosts or merging history.

## Results And Safety

- `accepted`/`submitting` do not prove UI or capture. Confirmation, countdown,
  recording, finalization and terminal states differ. Only `completed` with
  `publication_confirmed` delivers matching-ID/path, positive-byte, plausible
  silent MP4/H.264 metadata. A stop acknowledgement is not saved-output proof.
- Cancellation, selection timeout, refusal, expiry and unknown outcomes never
  trigger another recording, stop or startup. Keep the original IDs; never reset
  evidence, mint a replacement UUID or guess the latest task. Unknown startup
  and initialization remain durable until explicitly resolved and validated.
- EOF, transport cancellation and ending observation do not mean Stop, refusal,
  shutdown or cleanup. Dialogs/capture may remain. Complete client exit is a
  different event; host survival through outer process/Job retirement is not
  guaranteed by ordinary EOF tests.
- No output-directory scan, business SQL read or separate recording script is a
  normal plugin fallback for unavailable evidence. Deleted media stays deleted.

Schema-1 association records live at `<binding-file>.operations/`; independent
stop intent/receipt records live in `stops/`. Flushed intent precedes side effects.
Persistent worker/intent markers prevent replay after missing records/restart;
checksums detect corruption, not malicious replacement authentication.

Admission is sequential, including unresolved operations, with at most 128
records. The operation gate has a 1.5-second contention budget. A temporarily
unavailable body may coordinate once for the same ID; permanent absence and bad
content remain distinct failures. Four query and four stop observations per MCP
process are bounded separately. Query budget is eight seconds, stop nine, CLI
readiness observation 15, selection POST 130 and worker 155 seconds. Responses
are limited to 64 KiB and headers to 16 KiB. Trusted user-stopped clips may be
shorter than planned; natural completion retains its duration tolerance.

Transport verifies the pinned process generation/image/SID, instance,
capabilities and TCP peer ownership before sending credentials. Proxying,
redirects, cookies and submission reconnect are disabled. These checks do not
authenticate arbitrary same-version binaries or override local consent.

## Build And Validation

Developers need .NET 8 SDK and PowerShell 7.2+. End users need no SDK, Node or
Python: the adapter is self-contained and pins C# SDK Core 2.4.0. Licenses and
notices are included.

```powershell
pwsh -NoProfile -File scripts/build-codex-plugin.ps1
dotnet test tests/AgentRecorder.Mcp.Tests/AgentRecorder.Mcp.Tests.csproj -c Release -p:UseSharedCompilation=false
```

From a clean committed checkout, `scripts/build-codex-plugin-release.ps1` builds
the marketplace ZIP and checksum, embeds the source commit in `RELEASE.json`,
checks the archive inventory and verifies the assembly's commit metadata. It
does not upload, install, start Recorder or record anything.

Each build uses a fresh directory under `.local-data/plugin-builds`. An explicit
`-OutputDirectory` must be an unused child there; `-VerifyOnly` checks an existing
build without overwriting. Isolated test paths and retained historical inputs
have narrower exceptions, not arbitrary disk access.

The builder validates vendored schemas, contained `./` command, `${PLUGIN_ROOT}`
CWD, dependencies/licenses, private-file exclusion and actual EXE EOF behavior
with a unique private binding. It creates a marketplace root, not an installation.
Schema validity alone does not prove client discovery. See the
[official local workflow](https://developers.openai.com/plugins/build/plugins).

Source tests use private synthetic packages and require no old binaries.
Optional `AgentRecorder.Mcp.UpgradeProbe` requires explicit hash-matched historical
inputs; cleanup may remove them, and it must then report unavailable rather than
fake an old build or silently skip. Reviewed old-writer/new-reader checks covered
0.2.1 through 0.3.2 with Recorder 0.1.25, not downgrade or all future versions.

The local MVP stages had actual installed-plugin startup, silent recording/output,
exact-ID early-stop and 0.3.2 post-upgrade binding checks. Version 0.3.3 changes
packaging, documentation and version metadata, not capture behavior. Synthetic
results are not new human recording acceptance, directory review approval or a
complete client-exit survival test.
