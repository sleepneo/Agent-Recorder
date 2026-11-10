using System.Text.Json.Nodes;
using AgentRecorder.Infrastructure;
using AgentRecorder.Capture;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    private long _nextIndependentWindowNumber;
    public IndependentWindowPolicy IndependentWindowPolicy { get; } = global::AgentRecorder.Core.IndependentWindowPolicy.ReadEnvironment();
    // Mutated only under the Engine lock. Exact Recording instances, not history rows.
    private sealed record IndependentWindowReservation(Recording Recording, long PixelsPerSecond);
    private readonly Dictionary<string, IndependentWindowReservation> _independentWindowReservations = new();

    public IndependentWindowBudgetSnapshot GetIndependentWindowBudget()
    {
        lock (_lock)
        {
            // Engine -> output is the existing admission order. Only immutable
            // registrations and Task completion facts are read here. No worker locks.
            lock (_ordinaryOutputLock)
            {
                foreach (var reservation in _independentWindowReservations.Values.ToArray())
                {
                    var rec = reservation.Recording;
                    if (rec.IndependentWindowAdmissionPending || !rec.IsFinalized || _ordinaryOutputCleanup.ContainsKey(rec.Id) ||
                        _ordinaryBundleOutputUses.Any(use => ReferenceEquals(use.Recording, rec))) continue;
                    var retired = rec.Backend is WgcContinuousCaptureBackend wgc
                        ? wgc.OutputRetirement.IsCompletedSuccessfully
                        : rec.Backend is null && rec.OrdinaryBackendCreation?.NoResourcesRetired.IsCompletedSuccessfully == true;
                    // Unknown backend/dispatch/cleanup remains a reservation.
                    if (retired) _independentWindowReservations.Remove(rec.Id);
                }
            }
            int reserved = _independentWindowReservations.Count;
            int finishing = 0;
            long pixels = 0, finishingPixels = 0;
            foreach (var reservation in _independentWindowReservations.Values)
            {
                pixels = checked(pixels + reservation.PixelsPerSecond);
                var rec = reservation.Recording;
                if (rec.IsFinalized || rec.State is RecState.stopping or RecState.finalizing or RecState.completed or RecState.failed or RecState.cancelled)
                { finishing++; finishingPixels = checked(finishingPixels + reservation.PixelsPerSecond); }
            }
            return new(reserved, finishing,
                IndependentWindowPolicy.Valid ? checked(IndependentWindowPolicy.ConfiguredLimit!.Value - reserved) : null,
                pixels, finishingPixels,
                IndependentWindowPolicy.Valid ? checked(IndependentWindowPolicy.ConfiguredPixelsPerSecond!.Value - pixels) : null);
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
    private void ValidateIndependentWindowAdmission(nint? target, IReadOnlyList<Recording> active, long? requestedPixelsPerSecond = null)
    {
        if (!IndependentWindowPolicy.Valid)
            throw new ApiException(503, "INDEPENDENT_WINDOW_CONFIGURATION_INVALID",
                "Independent window startup configuration is invalid. Count must be 1 through 10; pixels per second must be a positive decimal Int64 integer.",
                new { reason_code = "invalid_host_configuration", environment_variable = IndependentWindowPolicy.InvalidEnvironmentVariable });
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
        if (requestedPixelsPerSecond is { } cost &&
            !IndependentWindowPixelRateFits(budget.ReservedPixelsPerSecond, cost, IndependentWindowPolicy.ConfiguredPixelsPerSecond!.Value))
            throw new ApiException(409, "INDEPENDENT_WINDOW_PIXEL_BUDGET_EXCEEDED",
                "The requested physical window pixel rate exceeds the remaining independent window admission budget.",
                new { reason_code = "independent_window_pixel_budget_exceeded", requested_pixels_per_second = cost,
                    reserved_pixels_per_second = budget.ReservedPixelsPerSecond,
                    configured_pixels_per_second = IndependentWindowPolicy.ConfiguredPixelsPerSecond.Value,
                    remaining_pixels_per_second = budget.RemainingPixelsPerSecond });
    }

    internal static bool IndependentWindowPixelRateFits(long reserved, long requested, long configured)
    {
        if (reserved < 0 || requested <= 0 || configured <= 0) return false;
        try { return checked(reserved + requested) <= configured; }
        catch (OverflowException) { return false; }
    }

    internal static long CalculateIndependentWindowPixelRate(CapturePlan? plan, int fps)
    {
        if (plan is not { IsWindowSurface: true, SourceKind: "window", TargetWindowSurfaceBounds: { Width: > 0, Height: > 0 } surface } ||
            fps is not (15 or 24 or 30 or 60))
            throw InvalidIndependentWindowPlan();
        try { return checked((long)surface.Width * surface.Height * fps); }
        catch (OverflowException) { throw InvalidIndependentWindowPlan(); }
    }

    private static ApiException InvalidIndependentWindowPlan() => new(400, "INDEPENDENT_WINDOW_CAPTURE_PLAN_INVALID",
        "A reliable positive physical window surface and supported configured frame rate are required; pixel cost cannot be unknown or overflow Int64.",
        new { reason_code = "independent_window_capture_plan_invalid" });

    private void SettleIndependentWindowAdmission(Recording rec)
    {
        lock (_lock)
            if (_independentWindowReservations.TryGetValue(rec.Id, out var reservation) && ReferenceEquals(reservation.Recording, rec))
                rec.IndependentWindowAdmissionPending = false;
    }
}
