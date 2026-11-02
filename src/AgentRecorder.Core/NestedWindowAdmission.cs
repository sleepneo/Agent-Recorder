using AgentRecorder.Infrastructure;

namespace AgentRecorder.Core;

public sealed partial class RecordingEngine
{
    // Called at both ordinary admission checks with the engine lock held.
    // Only frozen request/recording facts: no window query, backend call or wait.
    private static void ValidateNestedWindowAdmission(bool window, nint? hwnd, string role,
        string? parentId, IReadOnlyList<Recording> active)
    {
        if (!window && !active.Any(r => r.Config.RequireWindowSurface)) return;
        if (!window || active.Any(r => !r.Config.RequireWindowSurface || r.Config.AudioRequested || r.NestedRole is null))
            throw new ApiException(400, "INVALID_ARGUMENT", "Nested window capture requires two silent strict windows; mixed targets are unsupported.");
        if (role == "outer" && active.Count != 0)
            throw new ApiException(409, "RECORDING_ALREADY_RUNNING", "A window outer requires an idle ordinary recording slot.");
        if (role == "inner" && !active.Any(r => r.Id == parentId && r.Config.RequireWindowSurface && r.NestedRole == "outer"))
            throw new ApiException(400, "INVALID_ARGUMENT", "A window inner requires a strict window outer.");
        if (hwnd is { } target && active.Any(r => r.Config.WindowHandle == target))
            throw new ApiException(409, "WINDOW_TARGET_IN_USE", "The selected window is already used by the active nested recording.");
    }
}
