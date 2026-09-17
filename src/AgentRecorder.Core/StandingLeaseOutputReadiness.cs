using System.IO;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// The trusted output readiness result shared by local setup and the standing
/// execution environment. The check does not create or change the user target
/// directory or frozen target file; it may create and delete a non-capture
/// writability probe in an existing directory.
/// </summary>
internal sealed record StandingLeaseOutputReadinessResult(
    bool IsReady,
    string ReasonCode,
    StandingLeaseOutputFileSystemSnapshot Snapshot)
{
    internal static StandingLeaseOutputReadinessResult Ready(
        StandingLeaseOutputFileSystemSnapshot snapshot) =>
        new(true, "", snapshot);

    internal static StandingLeaseOutputReadinessResult Rejected(
        string reasonCode,
        StandingLeaseOutputFileSystemSnapshot snapshot) =>
        new(false, reasonCode, snapshot);
}

internal interface IStandingLeaseOutputReadinessProvider
{
    StandingLeaseOutputReadinessResult Check(
        string outputDirectory,
        string frozenFileName,
        TimeSpan reservedDuration);
}

internal sealed class SystemQueryStandingLeaseOutputReadinessProvider : IStandingLeaseOutputReadinessProvider
{
    internal static readonly SystemQueryStandingLeaseOutputReadinessProvider Instance = new();

    private SystemQueryStandingLeaseOutputReadinessProvider()
    {
    }

    public StandingLeaseOutputReadinessResult Check(
        string outputDirectory,
        string frozenFileName,
        TimeSpan reservedDuration)
    {
        var snapshot = CreateUnavailableSnapshot();
        try
        {
            var normalizedDirectory = StandingLeaseOutputPath.NormalizeDirectory(outputDirectory);
            var normalizedFileName = AuthorizedFixedRegionScope.NormalizeFrozenFileNameForAuthorization(frozenFileName);
            var frozenFilePath = Path.GetFullPath(Path.Combine(normalizedDirectory, normalizedFileName));
            snapshot = new StandingLeaseOutputFileSystemSnapshot(
                normalizedDirectory,
                frozenFilePath,
                directoryExists: false,
                frozenFileExists: false,
                directoryWritable: false,
                freeSpaceAvailable: false,
                availableFreeBytes: 0,
                requiredFreeBytes: RecordingPreflightChecker.RequiredFreeSpaceBytes(reservedDuration));

            if (!Directory.Exists(normalizedDirectory))
                return StandingLeaseOutputReadinessResult.Rejected(
                    "execution_output_directory_unavailable", snapshot);

            var directoryWritable = TryWriteNonCaptureProbe(normalizedDirectory);
            var frozenFileExists = File.Exists(frozenFilePath) || Directory.Exists(frozenFilePath);
            var requiredFreeBytes = RecordingPreflightChecker.RequiredFreeSpaceBytes(reservedDuration);
            var freeSpaceAvailable = false;
            var availableFreeBytes = 0L;
            try
            {
                freeSpaceAvailable = RecordingPreflightChecker.FreeSpaceProvider(
                    normalizedDirectory,
                    out availableFreeBytes) && availableFreeBytes >= 0;
            }
            catch
            {
                freeSpaceAvailable = false;
                availableFreeBytes = 0;
            }

            snapshot = new StandingLeaseOutputFileSystemSnapshot(
                normalizedDirectory,
                frozenFilePath,
                directoryExists: true,
                frozenFileExists,
                directoryWritable,
                freeSpaceAvailable,
                availableFreeBytes,
                requiredFreeBytes);

            if (!directoryWritable)
                return StandingLeaseOutputReadinessResult.Rejected(
                    "execution_output_directory_unwritable", snapshot);
            if (frozenFileExists)
                return StandingLeaseOutputReadinessResult.Rejected(
                    "execution_output_file_exists", snapshot);
            if (!freeSpaceAvailable)
                return StandingLeaseOutputReadinessResult.Rejected(
                    "execution_disk_space_unavailable", snapshot);
            if (availableFreeBytes < requiredFreeBytes)
                return StandingLeaseOutputReadinessResult.Rejected(
                    "execution_disk_space_insufficient", snapshot);

            return StandingLeaseOutputReadinessResult.Ready(snapshot);
        }
        catch (Phase3DomainException exception)
        {
            return StandingLeaseOutputReadinessResult.Rejected(exception.ReasonCode, snapshot);
        }
        catch
        {
            return StandingLeaseOutputReadinessResult.Rejected(
                "execution_output_environment_unavailable", snapshot);
        }
    }

    private static bool TryWriteNonCaptureProbe(string directory)
    {
        var probePath = Path.Combine(
            directory,
            ".agent-recorder-standing-output-readiness-" + Guid.NewGuid().ToString("N") + ".tmp");
        var probeSucceeded = false;
        try
        {
            using (new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.SequentialScan))
            {
            }
            probeSucceeded = true;
        }
        catch
        {
            probeSucceeded = false;
        }

        try
        {
            if (File.Exists(probePath))
                File.Delete(probePath);
        }
        catch
        {
            // Fail closed if a probe artifact cannot be removed. Never create
            // a directory or silently redirect the frozen output path.
            probeSucceeded = false;
        }

        return probeSucceeded && !File.Exists(probePath);
    }

    private static StandingLeaseOutputFileSystemSnapshot CreateUnavailableSnapshot() =>
        new(
            null,
            null,
            directoryExists: false,
            frozenFileExists: false,
            directoryWritable: false,
            freeSpaceAvailable: false,
            availableFreeBytes: 0,
            requiredFreeBytes: 0);
}
