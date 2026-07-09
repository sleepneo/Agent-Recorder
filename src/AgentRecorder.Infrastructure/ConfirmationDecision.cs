namespace AgentRecorder.Infrastructure;

/// <summary>
/// Represents the result of a local user confirmation interaction.
/// Only the local UI can produce this decision; HTTP API cannot approve or reject.
/// </summary>
public sealed record ConfirmationDecision(
    bool Approved,
    string? OutputDirectory = null,
    bool RememberOutputDirectory = false)
{
    /// <summary>
    /// Creates an approval decision with optional output directory override and
    /// whether to remember the directory as the new default.
    /// </summary>
    public static ConfirmationDecision Approve(
        string? outputDirectory = null,
        bool rememberOutputDirectory = false) =>
        new(true, outputDirectory, rememberOutputDirectory);

    /// <summary>
    /// Creates a rejection decision.
    /// </summary>
    public static ConfirmationDecision Reject() => new(false);
}
