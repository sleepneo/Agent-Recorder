namespace AgentRecorder.Capture;

/// <summary>
/// Pluggable process-execution contract for WGC helper.
/// Enables unit tests to supply a fake that captures arguments
/// instead of launching wgc-native-helper.exe.
/// </summary>
public interface IWgcHelperProcessRunner
{
    /// <summary>Run a helper process with the provided start info; return exit code + stdout/stderr.</summary>
    WgcHelperProcessResult Run(
        string fileName,
        IReadOnlyList<string> argumentList,
        int timeoutMs,
        CancellationToken cancellationToken = default);
}
