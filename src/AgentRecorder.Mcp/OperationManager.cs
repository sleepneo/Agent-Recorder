using System.Diagnostics;
using System.Text.Json;

namespace AgentRecorder.Mcp;

internal sealed partial class OperationManager(BindingStore bindingStore, RecorderTools tools) : IAsyncDisposable
{
    private readonly OperationStore store = new(bindingStore);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> workers = [];
    private readonly SemaphoreSlim queries = new(4);
    // Synthetic tests can fix the read/publication/retirement order. Never set by
    // tools, configuration, environment variables, or the production server.
    internal Action? AfterOperationRead { get; init; }
    internal sealed record CallFailure(string Stage, string Tool, string ExceptionType, int HResult, string[] Frames);
    internal Action<CallFailure>? FailureObserver { get; init; }

    public static bool OperationIdValid(string? id) => Guid.TryParseExact(id, "D", out var guid) && guid != Guid.Empty && guid.ToString("D") == id;
    public static bool NativeId(string? id, string prefix) => id is not null && id.Length == prefix.Length + 12 &&
        id.StartsWith(prefix, StringComparison.Ordinal) && id[prefix.Length..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<OperationResult> CallAsync(string name, IDictionary<string, JsonElement>? args, CancellationToken token)
    {
        string id = "";
        try
        {
            args ??= new Dictionary<string, JsonElement>();
            var allowed = name == "recorder_record_region" ? new[] { "operation_id", "duration_seconds" } : ["operation_id"];
            if (args.Keys.Except(allowed, StringComparer.Ordinal).Any() || !args.TryGetValue("operation_id", out var operation) ||
                operation.ValueKind != JsonValueKind.String || !OperationIdValid(operation.GetString()))
                throw new ContractException("invalid_arguments");
            id = operation.GetString()!;
            int duration = 10;
            if (name == "recorder_record_region" && args.TryGetValue("duration_seconds", out var seconds) &&
                (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out duration) || duration is < 1 or > 30))
                throw new ContractException("invalid_arguments");
            token.ThrowIfCancellationRequested();
            return name == "recorder_record_region" ? await AcceptAsync(id, duration, token) : await GetAsync(id, token);
        }
        catch (ContractException error) { return new(false, error.Code is "operation_record_missing" or "operation_record_corrupt" or "operation_record_unavailable" or "initialization_unknown" or "host_start_unknown" ? "unknown" : "invalid", error.Code, id); }
        catch (OperationCanceledException) { return new(false, "unknown", "operation_observation_cancelled", id); }
        catch (Exception error)
        {
            // Opt-in private in-process fixtures only. Never expose exception
            // messages, data, filesystem paths or these frames in tool output.
            FailureObserver?.Invoke(new("operation_dispatch", name, error.GetType().Name, error.HResult,
                new StackTrace(error, false).GetFrames().Take(8).Select(frame =>
                {
                    var method = frame.GetMethod(); var text = method?.DeclaringType?.Name + "." + method?.Name;
                    return text.Length > 128 ? text[..128] : text;
                }).ToArray()));
            return new(false, "unknown", "operation_store_unavailable", id);
        }
    }

    private async Task<OperationResult> AcceptAsync(string id, int duration, CancellationToken token)
    {
        var raw = bindingStore.Read();
        if (raw == null) return new(false, "unconfigured", "binding_unconfigured", id);
        store.ValidateLocation(raw);
        var prior = store.Read(id);
        if (prior == null && store.HasEvidence(id)) throw new ContractException("operation_record_missing");
        if (prior != null)
        {
            if (prior.DurationSeconds != duration || !OperationStore.SameBinding(prior.Binding, raw)) return From(prior, false, "conflict", "operation_conflict");
            return await GetAsync(id, token); // Replay is observation only, including across MCP restart.
        }
        var check = await tools.CallAsync("recorder_binding_get", null, token);
        if (!check.Ok || check.Binding == null)
        {
            // Another process may have admitted this key and started its bounded ensure.
            // Prefer that durable operation to a transient binding startup state.
            prior = store.Read(id);
            if (prior != null && prior.DurationSeconds == duration && OperationStore.SameBinding(prior.Binding, raw)) return await GetAsync(id, token);
            return new(false, check.Status, check.ReasonCode, id);
        }
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token); admission.CancelAfter(2000);
        using var gate = await store.LockAsync(admission.Token);
        raw = bindingStore.Read() ?? throw new ContractException("binding_unconfigured");
        store.ValidateLocation(raw);
        prior = store.ReadUnderGate(id);
        if (prior == null && store.HasEvidence(id)) throw new ContractException("operation_record_missing");
        if (prior != null)
            return prior.DurationSeconds != duration || !OperationStore.SameBinding(prior.Binding, raw)
                ? From(prior, false, "conflict", "operation_conflict") : LocalUnderGate(prior);
        BindingStore.Validate(raw); // Only a genuinely new admission depends on current startup state.
        var records = store.EntriesUnderGate().ToArray();
        if (records.Length >= OperationStore.MaximumRecords) return new(false, "invalid", "operation_capacity_exceeded", id);
        // This MVP is sequential. Unknown operations also occupy admission until resolved;
        // a new UUID must not turn a lost response into another selection/recording.
        if (records.Any(entry => !entry.Terminal)) return new(false, "busy", "operation_in_progress", id);
        admission.Token.ThrowIfCancellationRequested();
        var entry = new OperationEntry { SchemaVersion = 1, OperationId = id, DurationSeconds = duration,
            Binding = raw, OwnerPid = Environment.ProcessId, OwnerStartedTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            Status = "accepted", ReasonCode = "operation_accepted" };
        var lease = store.WorkerLease(id);
        try { store.Write(entry); }
        catch { lease.Dispose(); throw; }
        // Cancellation after this reliable admission cannot resubmit or mean product Stop.
        var task = Task.Run(() => WorkAsync(entry, lease));
        lock (workers) workers.Add(task);
        _ = task.ContinueWith(done => { lock (workers) workers.Remove(done); }, TaskScheduler.Default);
        return From(entry, true);
    }

    private async Task WorkAsync(OperationEntry entry, FileStream lease)
    {
        using (lease)
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            budget.CancelAfter(TimeSpan.FromSeconds(155));
            try
            {
                var ensured = await tools.CallAsync("recorder_host_ensure", null, budget.Token);
                if (!ensured.Ok || ensured.Binding == null)
                {
                    entry = entry with { ProductErrorCode = ensured.ProductErrorCode };
                    throw new ContractException(ensured.ReasonCode);
                }
                if (!OperationStore.SameBinding(entry.Binding, ensured.Binding)) throw new ContractException("operation_conflict");
                using var host = await BoundHost.OpenAsync(ensured.Binding, null, budget.Token);
                using var bindingLease = await bindingStore.AcquireAsync(budget.Token);
                var currentBinding = bindingStore.Read() ?? throw new ContractException("binding_unconfigured");
                store.ValidateLocation(currentBinding); BindingStore.Validate(currentBinding);
                if (!OperationStore.SameBinding(ensured.Binding, currentBinding)) throw new ContractException("operation_conflict");
                entry = entry with { Binding = ensured.Binding, Host = host.Stamp, Status = "submitting", ReasonCode = "submission_intent" };
                await SaveAsync(entry, budget.Token); // Durable, flushed intent must precede HTTP side effect.
                budget.Token.ThrowIfCancellationRequested();
                var body = JsonSerializer.Serialize(new
                {
                    target = new { type = "selected_region", selection_timeout_seconds = 120 },
                    duration_seconds = entry.DurationSeconds, countdown_seconds = 3,
                    audio = new { microphone = new { enabled = false }, system_audio = new { enabled = false } }
                });
                var response = await host.SendAsync(HttpMethod.Post, "recordings/quick", body, budget.Token);
                if (response.HttpStatus is 400 or 401 or 403 or 404 or 409 or 429 && response.Json.TryGetProperty("ok", out var rejected) && rejected.ValueKind == JsonValueKind.False)
                {
                    entry = entry with { Status = "not_submitted", ReasonCode = "product_request_rejected", Terminal = true,
                        ProductErrorCode = ErrorCode(response.Json.GetProperty("error"), "code") };
                }
                else
                {
                    var data = Data(response);
                    var state = Text(data, "status");
                    var recording = Text(data, "recording_id");
                    var confirmation = Text(data, "confirmation_id");
                    if (recording == null && state is "selection_cancelled" or "selection_timeout" or "selection_failed" or "display_unavailable" &&
                        data.GetProperty("quick").GetProperty("recording_created").ValueKind == JsonValueKind.False)
                        entry = entry with { Status = "no_recording", ProductStatus = state, ReasonCode = state, Terminal = true };
                    else if (NativeId(recording, "rec_"))
                        entry = entry with { RecordingId = recording, ConfirmationId = NativeId(confirmation, "confirm_") ? confirmation : null,
                            Status = confirmation != null && !NativeId(confirmation, "confirm_") ? "unknown" : Map(state),
                            ProductStatus = KnownState(state) ? state : null, ReasonCode = "native_recording_mapped" };
                    else throw new ContractException("native_mapping_unconfirmed");
                }
                await SaveAsync(entry, budget.Token);
            }
            catch (Exception error)
            {
                entry = entry with { Status = "unknown", ReasonCode = SafeReason(error) };
                try { using var finish = new CancellationTokenSource(1500); await SaveAsync(entry, finish.Token); } catch { }
            }
        }
    }

    private async Task<OperationResult> GetAsync(string id, CancellationToken token)
    {
        var entry = store.Read(id);
        if (entry == null) return store.HasEvidence(id) ? new(false, "unknown", "operation_record_missing", id) : new(false, "not_found", "operation_not_found", id);
        AfterOperationRead?.Invoke();
        bool retired = false;
        if (entry.RecordingId == null) (entry, retired) = ObserveOwner(entry);
        var raw = bindingStore.Read();
        if (raw == null || !OperationStore.SameBinding(entry.Binding, raw)) return From(entry, false, "conflict", "operation_conflict");
        store.ValidateLocation(raw);
        if (entry.RecordingId == null) return LocalResult(entry, retired);
        if (!await queries.WaitAsync(0, token)) return From(entry, false, "busy", "operation_query_busy");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(8000);
            var check = await tools.CallAsync("recorder_binding_get", null, deadline.Token);
            if (!check.Ok || check.Binding == null) return From(entry, false, "unknown", check.ReasonCode);
            if (entry.Host == null) return From(entry, false, "unknown", "native_mapping_unconfirmed");
            using var host = await BoundHost.OpenAsync(check.Binding, entry.Host, deadline.Token);
            var state = Data(await host.SendAsync(HttpMethod.Get, "recordings/" + entry.RecordingId, null, deadline.Token));
            var nativeStatus = Text(state, "status");
            if (Text(state, "recording_id") != entry.RecordingId || !KnownState(nativeStatus)) throw new ContractException("recording_evidence_unconfirmed");
            entry = entry with { Status = Map(nativeStatus), ProductStatus = nativeStatus,
                ReasonCode = "recording_state_observed", ProductErrorCode = ErrorCode(state, "stop_reason"),
                Terminal = nativeStatus is "completed" or "failed" or "cancelled" or "rejected" or "expired" };
            await SaveAsync(entry, deadline.Token);
            if (nativeStatus != "completed") return From(entry, nativeStatus is not ("failed" or "cancelled" or "rejected" or "expired" or "interrupted_unknown"));
            var outputReply = Data(await host.SendAsync(HttpMethod.Get, "recordings/" + entry.RecordingId + "/output", null, deadline.Token));
            return ConfirmOutput(entry, state, outputReply);
        }
        catch (Exception error) { return From(entry, false, "unknown", SafeReason(error)); }
        finally { queries.Release(); }
    }

    private OperationResult Local(OperationEntry entry)
    {
        var (observed, retired) = ObserveOwner(entry);
        return LocalResult(observed, retired);
    }

    private OperationResult LocalUnderGate(OperationEntry entry)
    {
        var (observed, retired) = ObserveOwner(entry, gateHeld: true);
        return LocalResult(observed, retired);
    }

    private (OperationEntry Entry, bool Retired) ObserveOwner(OperationEntry entry, bool gateHeld = false)
    {
        if (entry.Status is not ("accepted" or "submitting") || store.WorkerAlive(entry)) return (entry, false);
        // A completed worker publishes before releasing its lease. A retirement
        // observed after the first read therefore requires exactly one same-ID
        // reread, not an inference from that older snapshot. No polling or HTTP.
        var current = (gateHeld ? store.ReadUnderGate(entry.OperationId) : store.Read(entry.OperationId)) ?? throw new ContractException("operation_record_missing");
        if (current.DurationSeconds != entry.DurationSeconds || current.OwnerPid != entry.OwnerPid ||
            current.OwnerStartedTicks != entry.OwnerStartedTicks || !OperationStore.SameBinding(entry.Binding, current.Binding))
            throw new ContractException("operation_conflict");
        if (entry.Host != null && current.Host != entry.Host) throw new ContractException("operation_host_changed");
        var raw = bindingStore.Read();
        if (raw == null || !OperationStore.SameBinding(current.Binding, raw)) throw new ContractException("operation_conflict");
        store.ValidateLocation(raw);
        return (current, true);
    }

    private static OperationResult LocalResult(OperationEntry entry, bool retired) =>
        retired && entry.Status is "accepted" or "submitting"
            ? From(entry, false, "unknown", "operation_owner_retired_unknown")
            : From(entry, entry.Status is "accepted" or "submitting");

    private async Task SaveAsync(OperationEntry entry, CancellationToken token)
    {
        using var gate = await store.LockAsync(token);
        var current = store.ReadUnderGate(entry.OperationId) ?? throw new ContractException("operation_record_missing");
        if (current.OperationId != entry.OperationId || current.DurationSeconds != entry.DurationSeconds ||
            !OperationStore.SameBinding(current.Binding, entry.Binding)) throw new ContractException("operation_conflict");
        store.Write(entry);
    }

    private static OperationResult ConfirmOutput(OperationEntry entry, JsonElement state, JsonElement reply)
    {
        var id = entry.RecordingId!;
        OperationResult Unknown() => From(entry, false, "unknown", "publication_unconfirmed");
        static JsonElement Field(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) ? value : default;
        static string? StringField(JsonElement owner, string name) => Field(owner, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        static bool PositiveInteger(JsonElement owner, string name, out long value)
        { value = 0; var item = Field(owner, name); return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out value) && value > 0; }
        static bool PositiveNumber(JsonElement owner, string name, out double value)
        { value = 0; var item = Field(owner, name); return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out value) && double.IsFinite(value) && value > 0; }
        if (StringField(state, "recording_id") != id || StringField(state, "status") != "completed" || StringField(state, "mode") != "video" ||
            StringField(reply, "recording_id") != id || StringField(reply, "status") != "completed" || StringField(reply, "mode") != "video") return Unknown();
        var output = Field(reply, "output"); var initial = Field(state, "output");
        var path = StringField(output, "path");
        if (path == null || !Path.IsPathFullyQualified(path) || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            Field(output, "exists").ValueKind != JsonValueKind.True || StringField(output, "container") != "mp4" ||
            StringField(output, "codec") != "h264" || Field(output, "has_audio_stream").ValueKind != JsonValueKind.False ||
            !PositiveInteger(output, "size_bytes", out var size) || StringField(initial, "path") != path ||
            !PositiveInteger(initial, "bytes_written", out var written) || written != size ||
            !PositiveInteger(output, "width", out var width) || width > int.MaxValue ||
            !PositiveInteger(output, "height", out var height) || height > int.MaxValue || !PositiveNumber(output, "fps", out _))
            return Unknown();
        var probe = Field(output, "probe_streams");
        var tolerance = Math.Max(1.5, entry.DurationSeconds * .1);
        var userStopped = StringField(state, "stop_reason") is "user_requested" or "floating_button" or "tray_menu" or "global_hotkey";
        if (!PositiveNumber(output, "duration_seconds", out var duration) || duration > entry.DurationSeconds + tolerance ||
            !userStopped && Math.Abs(duration - entry.DurationSeconds) > tolerance || probe.ValueKind != JsonValueKind.Array) return Unknown();
        bool video = false;
        foreach (var stream in probe.EnumerateArray())
        {
            var type = StringField(stream, "codec_type"); var codec = StringField(stream, "codec_name");
            if (type == null || codec == null || type == "audio") return Unknown();
            if (type == "video" && codec == "h264") video = true;
        }
        if (!video) return Unknown();
        return new(true, "completed", "publication_confirmed", entry.OperationId, id, entry.ConfirmationId,
            "completed", entry.ProductErrorCode, path, size, duration);
    }

    private static OperationResult From(OperationEntry entry, bool ok, string? status = null, string? reason = null) =>
        new(ok, status ?? entry.Status, reason ?? entry.ReasonCode, entry.OperationId, entry.RecordingId, entry.ConfirmationId,
            entry.ProductStatus, entry.ProductErrorCode);
    private static JsonElement Data((int HttpStatus, JsonElement Json) response) =>
        response.HttpStatus is >= 200 and < 300 && response.Json.GetProperty("ok").ValueKind == JsonValueKind.True
            ? response.Json.GetProperty("data") : throw new ContractException("product_evidence_unavailable");
    private static string? Text(JsonElement value, string field) => value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool KnownState(string? status) => status is "requires_user_confirmation" or "created" or "pending_confirmation" or "preparing" or
        "countdown" or "recording" or "stopping" or "finalizing" or "completed" or "failed" or "cancelled" or "rejected" or "expired" or "interrupted_unknown";
    private static string Map(string? status) => status switch
    {
        "requires_user_confirmation" or "created" or "pending_confirmation" => "waiting_for_local_confirmation",
        "preparing" or "countdown" or "recording" or "stopping" or "finalizing" or "completed" or "failed" or "cancelled" or "rejected" or "expired" => status,
        _ => "unknown"
    };
    private static string? ErrorCode(JsonElement owner, string property) => Text(owner, property) is { } code && code is
        "INVALID_ARGUMENT" or "SOURCE_NOT_FOUND" or "UNAUTHORIZED" or "FORBIDDEN" or "UNSUPPORTED_FEATURE" or "WAIT_CAPACITY_EXCEEDED" or
        "storage_space_low" or "storage_capacity_unavailable" or "OUTPUT_PATH_IN_USE" or "encoder_start_failed" or "backend_creation_failed" or
        "unexpected_exit" or "user_rejected" or "confirmation_expired" or "duration_reached" or
        "user_requested" or "floating_button" or "tray_menu" or "global_hotkey" ? code : null;
    private static string SafeReason(Exception error) => error is ContractException contract ? contract.Code :
        error is OperationCanceledException ? "operation_observation_timeout_or_cancelled" : "operation_evidence_unavailable";

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        Task[] pending; lock (workers) pending = workers.ToArray();
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        lifetime.Dispose();
    }
}
