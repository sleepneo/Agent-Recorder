using System;
using System.IO;
using AgentRecorder.Core;

namespace AgentRecorder.App;

internal enum RecurringSetupOutputReadiness
{
    Ready, DirectoryUnavailable, DirectoryUnwritable, FreeSpaceUnavailable, InsufficientSpace,
}

internal interface IRecurringSetupOutputReadinessProvider
{
    RecurringSetupOutputReadiness Check(string outputDirectory, TimeSpan singleRunDuration);
}

// There is no frozen occurrence filename during setup. Check only the exact
// directory and one run's capacity; execution still checks its actual target.
internal sealed class RecurringSetupOutputReadinessProvider : IRecurringSetupOutputReadinessProvider
{
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, Stream> _createProbe;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<string> _deleteProbe;
    private readonly Func<string, (bool Available, long Bytes)> _freeSpace;

    internal RecurringSetupOutputReadinessProvider(
        Func<string, bool>? directoryExistsForTest = null,
        Func<string, Stream>? createProbeForTest = null,
        Func<string, bool>? fileExistsForTest = null,
        Action<string>? deleteProbeForTest = null,
        Func<string, (bool Available, long Bytes)>? freeSpaceForTest = null)
    {
        _directoryExists = directoryExistsForTest ?? Directory.Exists;
        _createProbe = createProbeForTest ?? (path =>
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose));
        _fileExists = fileExistsForTest ?? File.Exists;
        _deleteProbe = deleteProbeForTest ?? File.Delete;
        _freeSpace = freeSpaceForTest ?? (directory =>
        {
            var available = RecordingPreflightChecker.FreeSpaceProvider(directory, out var bytes);
            return (available, bytes);
        });
    }

    public RecurringSetupOutputReadiness Check(string outputDirectory, TimeSpan singleRunDuration)
    {
        bool directoryAvailable;
        try
        {
            directoryAvailable = _directoryExists(outputDirectory);
        }
        catch { return RecurringSetupOutputReadiness.DirectoryUnavailable; }
        if (!directoryAvailable) return RecurringSetupOutputReadiness.DirectoryUnavailable;

        string probe;
        try
        {
            probe = Path.Combine(outputDirectory,
                ".agent-recorder-recurring-readiness-" + Guid.NewGuid().ToString("N") + ".tmp");
        }
        catch { return RecurringSetupOutputReadiness.DirectoryUnavailable; }
        var probeCreatedAndClosed = false;
        try
        {
            using (_createProbe(probe)) { }
            probeCreatedAndClosed = true;
        }
        catch { }

        // DeleteOnClose is an optimization, not the cleanup contract. Always
        // explicitly delete this probe, including after partial creation or
        // a stream-close failure, and fail closed if it remains or deletion
        // cannot be confirmed.
        var probeRemoved = false;
        try
        {
            _deleteProbe(probe);
            probeRemoved = !_fileExists(probe);
        }
        catch { }
        if (!probeCreatedAndClosed || !probeRemoved)
            return RecurringSetupOutputReadiness.DirectoryUnwritable;

        try
        {
            var (available, bytes) = _freeSpace(outputDirectory);
            if (!available || bytes < 0) return RecurringSetupOutputReadiness.FreeSpaceUnavailable;
            return bytes >= RecordingPreflightChecker.RequiredFreeSpaceBytes(singleRunDuration)
                ? RecurringSetupOutputReadiness.Ready : RecurringSetupOutputReadiness.InsufficientSpace;
        }
        catch { return RecurringSetupOutputReadiness.FreeSpaceUnavailable; }
    }
}
