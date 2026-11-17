---
name: recorder-connection
description: Connect Recorder 0.1.25, request a silent 1-30 second selected-region recording with local approval, query its MP4, or explicitly stop only its exact mapped recording ID. No sound, window, plan, global stop or deletion tools.
---

Use the six available Recorder MCP tools for connection and short region clips. This
plugin requires a separately obtained Windows x64 self-contained Agent Recorder
portable package, exactly version 0.1.25. The plugin does not contain Recorder.

Plugin 0.3.2 preserves the same six tools and saved schema-1 evidence. A retired
worker triggers at most one same-operation evidence recheck before reporting an
unresolved submission. This is observation only and never authorizes another
recording or stop. Unknown results still require querying the same operation ID.

If the operation body is temporarily unavailable during publication, observation
may coordinate once with that same operation journal's existing gate. This does
not retry recording, stop or startup. A permanently missing body with a worker
marker still fails closed; unavailable does not mean corrupt or never submitted.

The shared binding lives at
`%LOCALAPPDATA%\AgentRecorderIntegrations\binding.json`, alongside the usual
Recorder data root rather than inside it. Binding configuration must stay outside
the selected package and data roots. Old default bindings are not automatically
discovered, moved or deleted. A location conflict is a configuration refusal and
must not be worked around by initializing or changing product data.

1. Call `recorder_binding_get`. It is read-only and does not start Recorder.
2. For first setup or an explicit change, ask the user for the external portable
   package root and absolute data directory. Do not infer either from the current
   project, scan disks, copy keys or move history. Explain that an existing data
   directory is used by default. To initialize a new directory, obtain the user's
   explicit intent, then set `initialize_new_data_directory=true` in
   `recorder_binding_set`. This prepares only an empty directory; Recorder itself
   initializes its data when explicitly started. Never repeat this flag to repair
   deleted or uncertain data. Rebinding changes the shared saved binding.
3. When the user requests startup/connection to the host, call
   `recorder_host_ensure` only after a valid binding. It runs the bound CLI with
   fixed tray mode and finite budgets, preserving its Windows user, desktop,
   identity and single-instance checks. No shell command from the user is needed.
4. Explain the result accurately. A `ready` result is CLI-verified host readiness
   and interactive desktop evidence. Human visibility and capture are unverified.
   Startup readiness alone does not establish capture. Local recording approval
   remains required; platform identity or an API key does
   not grant that approval or server-side isolation between platforms.

On `unknown`, cancellation, timeout or lost connection, do not retry startup,
reset the binding, select another package, close other instances or suggest
bypassing Windows protections. Preserve the returned reason and product error
code. Explain that observation ended without proving whether startup finished.
An explicit rebind to existing, verified data can be requested after the user
resolves the uncertainty. Binding loss or damaged data is an error, not consent
to create a new history. Do not expose or request the product API key.
Persisted `initialization_unknown` and `host_start_unknown` stay unknown on later
get/ensure calls. They do not mean invalid configuration or permission to rebind.

## Silent selected-region recording

For a request such as “选区录屏 10 秒，不录声音”, explain that the user will select
the region and approve locally, followed by the default three-second countdown.
This version supports only silent selected-region video for 1..30 whole seconds,
default 10. Do not substitute a window, display, last region, audio, plan or output
option. It cannot provide global stop, delete, history or screenshot operations.

1. Check the binding if needed and resolve setup through the binding tools above.
   Generate and retain one canonical lowercase UUID `operation_id` **before** the
   submission call. This UUID is a local association/deduplication key, not a
   native recording ID. The user does not need to handle internal IDs or commands.
2. Call `recorder_record_region` once with that UUID and duration. It admits the
   local operation promptly and performs bounded host readiness and selection
   observation in the background. `accepted` / `submitting` do not prove Recorder
   accepted the request, showed a window or started capture. Never approve through
   HTTP, ask for a key, or bypass the local selection/confirmation UI.
3. Query `recorder_operation_get` using the **same** UUID, normally about once a
   second. These reads never start the host or create another selection. The
   adapter retains native IDs only from product evidence and queries only the
   mapped recording. Waiting for local confirmation, preparing, countdown,
   recording and finalizing are separate states. Report them accurately.
4. Only `ok=true`, `status=completed`, `reason_code=publication_confirmed` delivers
   the MP4 path and reported size/duration. Return a concise human-facing result;
   do not expose keys, raw responses or internal journal details. No media content
   is read by the adapter and no attachments are generated.

On cancelled selection, selection timeout, rejection or expiry, report the
evidence and do not reopen selection automatically. On tool cancellation, EOF,
lost response, unknown or not_found, retain the original UUID and only query it.
Do not submit again, mint a new key, reset binding/operation files, guess the
latest recording or issue global Stop/revoke/shutdown. A known native ID remains
attached to the same operation; unknown without an ID is not “no recording”.
Do not scan directories, read SQLite or run a separate recording script to supply
a normal plugin result. Keep querying the original operation or report its uncertainty.
The adapter may have stopped observing while a local dialog or recording remains.
Do not claim it was cleaned up. A genuinely new user recording request must wait
for any prior operation to resolve. This sequential MVP deliberately blocks new
operations while an unresolved/unknown operation remains; it has no reset or
automatic cleanup tool.

## Explicitly stop this recording

Only when the user explicitly asks to stop, use `recorder_recording_stop` with
the original operation UUID and the exact native `recording_id` already returned
for it. Never guess an ID, enumerate tasks, use latest/output filenames, ensure a
new host or switch bindings to stop. The tool accepts no reason, force or approval.

The stop call has a nine-second observation budget. `not_sent` means this call
did not submit; `accepted` only means the request was acknowledged; `unknown`
means submission/receipt may be uncertain; `rejected` is a known product refusal;
`already_terminal` means the recording had already ended and no new stop was sent.
None of these is proof of a saved video. Do not attribute natural completion to
this stop. Repeated/concurrent calls do not re-POST; after any uncertainty only
query `recorder_operation_get` with the original UUID.

Continue observing stopping/finalizing until native terminal/output evidence.
Only publication_confirmed delivers an MP4; an explicitly user-stopped completed
clip may be shorter than planned. A pre-capture stop may cancel with no video.
Cancellation, EOF, transport loss or the agent ending observation is never user
stop intent. Do not Stop All, approve/reject, revoke, shut down or kill anything.
This narrow tool does not provide server-side platform permission isolation.
