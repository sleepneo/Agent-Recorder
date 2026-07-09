using System.Collections.Generic;

namespace AgentRecorder.Core;

/// <summary>
/// Result of a preflight dry-run check. Stable error codes and suggested actions
/// are intended to be machine-readable so the AI agent can surface actionable
/// guidance to the user.
/// </summary>
internal sealed record RecordingPreflightResult(
    bool Passed,
    string? ErrorCode = null,
    string? Message = null,
    string? SuggestedAction = null,
    IReadOnlyList<string>? Warnings = null);
