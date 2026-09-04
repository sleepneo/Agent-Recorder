using System.IO;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Strict, side-effect-free comparison of a live environment snapshot with a
/// persisted fixed-region authorization scope. Every unknown or mismatched
/// field fails closed before the standing proof can be consumed.
/// </summary>
internal static class StandingLeaseExecutionEnvironmentValidator
{
    internal static bool TryValidate(
        StandingLeaseExecutionEnvironment? environment,
        AuthorizedFixedRegionScope? scope,
        out string failureReason)
    {
        failureReason = "execution_environment_missing";
        if (environment is null || scope is null)
            return false;

        if (!string.Equals(environment.CurrentUserSid, scope.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(environment.SessionBinding, scope.SessionBinding, StringComparison.Ordinal) ||
            !environment.IsInteractiveDesktop)
        {
            failureReason = "execution_environment_mismatch";
            return false;
        }

        if (scope.DisplayIdentityStatus != AuthorizedDisplayIdentityStatus.Resolved ||
            string.IsNullOrWhiteSpace(scope.StableDisplayFingerprint))
        {
            failureReason = "execution_display_identity_unavailable";
            return false;
        }

        if (environment.Displays.Count == 0 ||
            environment.Displays.Any(display =>
                display.IdentityStatus != DisplayIdentityResolutionStatus.Resolved ||
                string.IsNullOrWhiteSpace(display.StableDisplayFingerprint)))
        {
            failureReason = "execution_display_identity_unavailable";
            return false;
        }

        var matchingDisplays = environment.Displays
            .Where(display => string.Equals(
                display.StableDisplayFingerprint,
                scope.StableDisplayFingerprint,
                StringComparison.Ordinal))
            .ToArray();
        if (matchingDisplays.Length == 0)
        {
            failureReason = "execution_display_missing";
            return false;
        }

        if (matchingDisplays.Length != 1)
        {
            failureReason = "execution_display_ambiguous";
            return false;
        }

        var display = matchingDisplays[0];
        if (display.PhysicalBounds is null ||
            display.DpiX is not > 0 ||
            display.DpiY is not > 0 ||
            display.PhysicalWidth is not > 0 ||
            display.PhysicalHeight is not > 0 ||
            display.Orientation is null)
        {
            failureReason = "execution_display_metadata_unavailable";
            return false;
        }

        if (display.PhysicalBounds.Value != scope.DisplayBounds ||
            display.DpiX.Value != scope.DpiX ||
            display.DpiY.Value != scope.DpiY ||
            display.PhysicalWidth.Value != scope.PhysicalWidth ||
            display.PhysicalHeight.Value != scope.PhysicalHeight ||
            display.Orientation.Value != scope.Orientation)
        {
            failureReason = "execution_display_metadata_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(environment.TopologyDigest))
        {
            failureReason = "execution_topology_unavailable";
            return false;
        }

        if (!string.Equals(environment.TopologyDigest, scope.TopologyDigest, StringComparison.Ordinal))
        {
            failureReason = "execution_topology_mismatch";
            return false;
        }

        if (!TryValidateRegionProjection(scope, display.PhysicalBounds.Value, out failureReason))
            return false;

        if (!TryValidateOutput(scope, environment.OutputFileSystem, out failureReason))
            return false;

        failureReason = "";
        return true;
    }

    private static bool TryValidateRegionProjection(
        AuthorizedFixedRegionScope scope,
        AuthorizedPhysicalRectangle currentDisplayBounds,
        out string failureReason)
    {
        failureReason = "execution_region_invalid";
        try
        {
            var region = scope.RegionWithinDisplay;
            var projected = scope.VirtualScreenRegion;
            var expected = new AuthorizedPhysicalRectangle(
                checked(currentDisplayBounds.X + region.X),
                checked(currentDisplayBounds.Y + region.Y),
                region.Width,
                region.Height);

            if (projected != expected ||
                region.X < 0 ||
                region.Y < 0 ||
                checked(region.X + region.Width) > currentDisplayBounds.Width ||
                checked(region.Y + region.Height) > currentDisplayBounds.Height)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateOutput(
        AuthorizedFixedRegionScope scope,
        StandingLeaseOutputFileSystemSnapshot? output,
        out string failureReason)
    {
        failureReason = "execution_output_environment_unavailable";
        if (output is null)
            return false;

        string expectedDirectory;
        string expectedFile;
        try
        {
            expectedDirectory = StandingLeaseOutputPath.NormalizeDirectory(scope.OutputDirectory);
            expectedFile = Path.GetFullPath(scope.OutputFilePath);
        }
        catch
        {
            failureReason = "execution_output_directory_invalid";
            return false;
        }

        if (!string.Equals(output.NormalizedOutputDirectory, expectedDirectory, StringComparison.Ordinal))
        {
            failureReason = "execution_output_directory_mismatch";
            return false;
        }

        if (!string.Equals(output.FrozenOutputFilePath, expectedFile, StringComparison.Ordinal))
        {
            failureReason = "execution_output_file_path_mismatch";
            return false;
        }

        if (scope.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists)
        {
            failureReason = "execution_output_conflict_policy_mismatch";
            return false;
        }

        if (!output.DirectoryExists)
        {
            failureReason = "execution_output_directory_unavailable";
            return false;
        }

        if (!output.DirectoryWritable)
        {
            failureReason = "execution_output_directory_unwritable";
            return false;
        }

        if (output.FrozenFileExists)
        {
            failureReason = "execution_output_file_exists";
            return false;
        }

        var required = RecordingPreflightChecker.RequiredFreeSpaceBytes(scope.ReservedDuration);
        if (!output.FreeSpaceAvailable ||
            output.AvailableFreeBytes < 0 ||
            output.RequiredFreeBytes != required)
        {
            failureReason = "execution_disk_space_unavailable";
            return false;
        }

        if (output.AvailableFreeBytes < required)
        {
            failureReason = "execution_disk_space_insufficient";
            return false;
        }

        return true;
    }
}
