using System.Text.Json;

namespace AgentRecorder.Mcp;

internal sealed partial class OperationManager
{
    private readonly SemaphoreSlim stopQueries = new(4);

    public async Task<StopResult> StopCallAsync(IDictionary<string, JsonElement>? args, CancellationToken token)
    {
        string id = ""; string? recording = null; bool intent = false, slot = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(9));
        try
        {
            if (args == null || args.Count != 2 || args.Keys.Except(new[] { "operation_id", "recording_id" }, StringComparer.Ordinal).Any() ||
                !args.TryGetValue("operation_id", out var op) || op.ValueKind != JsonValueKind.String || !OperationIdValid(op.GetString()) ||
                !args.TryGetValue("recording_id", out var native) || native.ValueKind != JsonValueKind.String || !NativeId(native.GetString(), "rec_"))
                throw new ContractException("invalid_arguments");
            id = op.GetString()!; recording = native.GetString()!;
            budget.Token.ThrowIfCancellationRequested();
            var entry = store.Read(id);
            if (entry == null) return new(false, "not_sent", store.HasEvidence(id) ? "operation_record_missing" : "operation_not_found", id, recording);
            ValidateStopTarget(entry, recording);
            var stops = new StopStore(store);
            var previous = stops.Read(id); intent = previous != null;
            ValidateStopEvidence(previous, entry);
            if (!await stopQueries.WaitAsync(0, budget.Token)) return new(false, intent ? "unknown" : "not_sent", "stop_query_busy", id, recording);
            slot = true;
            var check = await tools.CallAsync("recorder_binding_get", null, budget.Token);
            if (!check.Ok || check.Binding == null) throw new ContractException(check.ReasonCode);
            if (!OperationStore.SameBinding(entry.Binding, check.Binding)) throw new ContractException("operation_conflict");
            using var host = await BoundHost.OpenAsync(check.Binding, entry.Host, budget.Token);
            var state = Data(await host.SendAsync(HttpMethod.Get, "recordings/" + recording, null, budget.Token));
            var productState = Text(state, "status");
            if (Text(state, "recording_id") != recording || Text(state, "mode") != "video" || !KnownState(productState))
                throw new ContractException("recording_evidence_unconfirmed");
            if (productState is "completed" or "failed" or "cancelled" or "rejected" or "expired" or "interrupted_unknown")
                return new(true, "already_terminal", "already_terminal", id, recording, productState, ErrorCode(state, "stop_reason"));
            if (previous != null) return ReplayStop(previous, productState);
            if (productState is "stopping" or "finalizing") return new(true, "not_sent", "recording_already_settling", id, recording, productState);

            // Keep cooperative rebinding out of the proven request. The global
            // operation gate is released before the bounded stop HTTP wait.
            using var bindingLease = await bindingStore.AcquireAsync(budget.Token);
            using (var gate = await store.LockAsync(budget.Token))
            {
                entry = store.ReadUnderGate(id) ?? throw new ContractException("operation_record_missing");
                ValidateStopTarget(entry, recording);
                if (entry.Host != host.Stamp) throw new ContractException("operation_host_changed");
                previous = stops.Read(id); intent = previous != null;
                ValidateStopEvidence(previous, entry);
                if (previous != null) return ReplayStop(previous, productState);
                budget.Token.ThrowIfCancellationRequested();
                // Conservative from this point: a partial marker/intent write
                // blocks replay too, even when the request was never sent.
                intent = true;
                stops.WriteIntent(new(1, id, recording, entry.Binding, host.Stamp, "intent", "stop_submission_intent"));
            }
            budget.Token.ThrowIfCancellationRequested();
            var response = await host.SendAsync(HttpMethod.Post, "recordings/" + recording + "/stop",
                "{\"reason\":\"user_requested\"}", budget.Token, TimeSpan.FromSeconds(5));
            string status, reason; string? error = null, observed = null;
            if (response.HttpStatus is 400 or 401 or 403 or 404 or 409 or 429 &&
                response.Json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                status = "rejected"; reason = "stop_request_rejected";
                error = ErrorCode(response.Json.GetProperty("error"), "code");
            }
            else
            {
                var reply = Data(response); observed = Text(reply, "status");
                if (Text(reply, "recording_id") != recording || Text(reply, "mode") != "video" ||
                    observed is not ("stopping" or "finalizing" or "completed" or "cancelled" or "failed" or "rejected" or "expired"))
                    throw new ContractException("stop_response_unconfirmed");
                status = "accepted"; reason = "stop_request_accepted"; error = ErrorCode(reply, "stop_reason");
            }
            using (var gate = await store.LockAsync(budget.Token))
            {
                var current = stops.Read(id) ?? throw new ContractException("stop_evidence_missing");
                ValidateStopEvidence(current, entry);
                if (current.Status != "intent") throw new ContractException("stop_evidence_conflict");
                stops.Write(current with { Status = status, ReasonCode = reason, ProductStatus = observed, ProductErrorCode = error });
            }
            return new(status == "accepted", status, reason, id, recording, observed, error);
        }
        catch (Exception error)
        {
            var reason = error is ContractException contract ? contract.Code :
                error is OperationCanceledException ? "stop_observation_timeout_or_cancelled" : "stop_evidence_unavailable";
            return new(false, intent || reason is "stop_evidence_corrupt" or "stop_evidence_missing" ? "unknown" : "not_sent", reason, id, recording);
        }
        finally { if (slot) stopQueries.Release(); }
    }

    private void ValidateStopTarget(OperationEntry entry, string recording)
    {
        if (entry.RecordingId == null) throw new ContractException("native_mapping_unconfirmed");
        if (entry.RecordingId != recording) throw new ContractException("operation_conflict");
        if (entry.Host == null) throw new ContractException("native_mapping_unconfirmed");
        var raw = bindingStore.Read() ?? throw new ContractException("binding_unconfigured");
        store.ValidateLocation(raw);
        if (!OperationStore.SameBinding(entry.Binding, raw)) throw new ContractException("operation_conflict");
        BindingStore.Validate(raw);
    }
    private static void ValidateStopEvidence(StopEntry? evidence, OperationEntry entry)
    {
        if (evidence != null && (evidence.RecordingId != entry.RecordingId || evidence.Host != entry.Host ||
            !OperationStore.SameBinding(evidence.Binding, entry.Binding))) throw new ContractException("stop_evidence_conflict");
    }
    private static StopResult ReplayStop(StopEntry entry, string? state) => new(entry.Status == "accepted",
        entry.Status == "intent" ? "unknown" : entry.Status,
        entry.Status == "intent" ? "stop_submission_unknown" : entry.ReasonCode,
        entry.OperationId, entry.RecordingId, state, entry.ProductErrorCode);
}
