using System;

namespace AgentRecorder.Core;

/// <summary>
/// Narrow, immutable read boundary for the controlled system-audio experiment.
/// The environment is read once by the composition root; request parsing does
/// not race on process-global environment state.
/// </summary>
public interface ISystemAudioExperimentFlag
{
    bool IsEnabled { get; }
}

public sealed class SystemAudioExperimentFlag : ISystemAudioExperimentFlag
{
    public const string EnvironmentVariableName = "AGENT_RECORDER_EXPERIMENTAL_SYSTEM_AUDIO";

    public SystemAudioExperimentFlag(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    public bool IsEnabled { get; }

    public static SystemAudioExperimentFlag FromEnvironment(Func<string?>? read = null)
    {
        var value = read == null
            ? Environment.GetEnvironmentVariable(EnvironmentVariableName)
            : read();
        return new SystemAudioExperimentFlag(IsExactTrue(value));
    }

    public static bool IsExactTrue(string? value)
        => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}

public sealed class DisabledSystemAudioExperimentFlag : ISystemAudioExperimentFlag
{
    public static DisabledSystemAudioExperimentFlag Instance { get; } = new();
    private DisabledSystemAudioExperimentFlag() { }
    public bool IsEnabled => false;
}
