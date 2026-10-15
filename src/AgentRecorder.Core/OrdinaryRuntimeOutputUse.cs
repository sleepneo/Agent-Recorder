namespace AgentRecorder.Core;

/// <summary>Process-local ordinary main-output claims, observed once for a bounded
/// set of already validated paths. Null means unavailable, never no claims.</summary>
public interface IOrdinaryRuntimeOutputUseProvider
{
    OrdinaryRuntimeOutputSnapshot? ReadSnapshot(IReadOnlyList<string> candidatePaths);
}

public sealed record OrdinaryRuntimeOutputUse(string Path, bool InUse);

/// <summary>One immutable answer per requested path; no owner identities or counts.</summary>
public sealed class OrdinaryRuntimeOutputSnapshot
{
    public const int MaxCandidates = 100;
    public const int MaxClaims = 500;
    public const int LockTimeoutMilliseconds = 50;
    public IReadOnlyList<OrdinaryRuntimeOutputUse> Paths { get; }

    public OrdinaryRuntimeOutputSnapshot(IEnumerable<OrdinaryRuntimeOutputUse> paths)
        => Paths = Array.AsReadOnly(paths.ToArray());
}
