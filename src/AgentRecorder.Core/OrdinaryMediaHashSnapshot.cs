namespace AgentRecorder.Core;

/// <summary>Content observed during Bundle generation; not current integrity verification.</summary>
public sealed record OrdinaryMediaHashSnapshot(string Algorithm, string Value, string Basis, long SizeBytes);
