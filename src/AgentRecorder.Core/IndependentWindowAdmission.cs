using System.Text.Json.Nodes;
using AgentRecorder.Infrastructure;
using AgentRecorder.Capture;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    private long _nextIndependentWindowNumber;
    public IndependentWindowPolicy IndependentWindowPolicy { get; } = global::AgentRecorder.Core.IndependentWindowPolicy.ReadEnvironment();
    // Mutated only under the Engine lock. Exact Recording instances, not history rows.
    private readonly Dictionary<string, Recording> _independentWindowReservations = new();

    public IndependentWindowBudgetSnapshot GetIndependentWindowBudget()
    {
        lock (_lock)
        {
            // Engine -> output is the existing admission order. Only immutable
            // registrations and Task completion facts are read here. No worker locks.
            lock (_ordinaryOutputLock)
            {
                foreach (var rec in _independentWindowReservations.Values.ToArray())
                {
                    if (!rec.IsFinalized || _ordinaryOutputCleanup.ContainsKey(rec.Id) ||
                        _ordinaryBundleOutputUses.Any(use => ReferenceEquals(use.Recording, rec))) continue;
                    var retired = rec.Backend is WgcContinuousCaptureBackend wgc
                        ? wgc.OutputRetirement.IsCompletedSuccessfully
                        : rec.Backend is null && rec.OrdinaryBackendCreation?.NoResourcesRetired.IsCompletedSuccessfully == true;
                    // Unknown backend/dispatch/cleanup remains a reservation.
                    if (retired) _independentWindowReservations.Remove(rec.Id);
                }
            }
            int reserved = _independentWindowReservations.Count;
            int finishing = _independentWindowReservations.Values.Count(r => r.IsFinalized ||
                r.State is RecState.stopping or RecState.finalizing or RecState.completed or RecState.failed or RecState.cancelled);
            return new(reserved, finishing, IndependentWindowPolicy.ConfiguredLimit is { } limit ? Math.Max(0,limit-reserved) : null);
        }
    }

    // Endpoint and profile/window-resolution context come from trusted callers,
    // never request fields or a preconstructed Recording's source alone.
    private static bool IsIndependentWindowRequest(JsonNode cfg, bool rawContext)
    {
        if (!rawContext || cfg is not JsonObject request || request.ContainsKey("nested") ||
            request.ContainsKey("parent_recording_id") || request.ContainsKey("session_id")) return false;
        if (request["source"] is not JsonObject source || source["type"] is not JsonValue type ||
            !type.TryGetValue<string>(out var name) || name != "window") return false;
        if (request.ContainsKey("mode") && (request["mode"] is not JsonValue mode ||
            !mode.TryGetValue<string>(out var modeName) || modeName != "video")) return false;
        if (cfg["audio"] is null) return true;
        if (cfg["audio"] is not JsonObject audio) return false;
        foreach (var channel in new[] { "microphone", "system_audio" })
        {
            if (audio[channel] is null) continue;
            if (audio[channel] is not JsonObject value) return false;
            if (value["enabled"] is null) continue;
            if (value["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var on) || on) return false;
        }
        return true;
    }

    // Both ordinary admission phases hold the existing engine lock. No provider,
    // backend/worker lock, filesystem operation or wait is performed here.
    private void ValidateIndependentWindowAdmission(nint? target, IReadOnlyList<Recording> active)
    {
        if (!IndependentWindowPolicy.Valid)
            throw new ApiException(503, "INDEPENDENT_WINDOW_CONFIGURATION_INVALID", "Independent window startup limit must be an integer from 1 through 10.",
                new { reason_code = "invalid_host_configuration", environment_variable = global::AgentRecorder.Core.IndependentWindowPolicy.EnvironmentVariable });
        if (active.Any(r => r.WindowIdentity is null || !r.Config.RequireWindowSurface ||
            r.Mode != "video" || r.Config.AudioRequested || r.NestedRole is not null))
            throw new ApiException(409, "RECORDING_ALREADY_RUNNING", "Independent windows cannot mix with other recording contracts.");
        var budget = GetIndependentWindowBudget();
        if (budget.Remaining == 0)
            throw new ApiException(409, "TOO_MANY_CONCURRENT_RECORDINGS", "Independent window budget is reserved, including unretired delivery work.",
                new { reason_code = "independent_window_budget_exhausted", configured_limit = IndependentWindowPolicy.ConfiguredLimit,
                    reserved = budget.Reserved, finalizing_or_unknown = budget.FinalizingOrUnknown, remaining = budget.Remaining });
        if (target is { } hwnd && active.Any(r => r.Config.WindowHandle == hwnd))
            throw new ApiException(409, "WINDOW_TARGET_IN_USE", "This fixed window is already used by an active recording.");
    }
}
