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

        return TryValidate(
            environment,
            FixedRegionExecutionEnvironmentRequirements.FromScope(scope),
            out failureReason);
    }

    internal static bool TryValidate(
        StandingLeaseExecutionEnvironment? environment,
        FixedRegionExecutionEnvironmentRequirements? requirements,
        out string failureReason)
    {
        failureReason = "execution_environment_missing";
        if (environment is null || requirements is null)
            return false;

        if (!string.Equals(environment.CurrentUserSid, requirements.CurrentUserSid, StringComparison.Ordinal) ||
            !string.Equals(environment.SessionBinding, requirements.SessionBinding, StringComparison.Ordinal) ||
            !environment.IsInteractiveDesktop)
        {
            failureReason = "execution_environment_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(requirements.StableDisplayFingerprint))
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
                requirements.StableDisplayFingerprint,
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

        if (display.PhysicalBounds.Value != requirements.DisplayBounds ||
            display.DpiX.Value != requirements.DpiX ||
            display.DpiY.Value != requirements.DpiY ||
            display.PhysicalWidth.Value != requirements.PhysicalWidth ||
            display.PhysicalHeight.Value != requirements.PhysicalHeight ||
            display.Orientation.Value != requirements.Orientation)
        {
            failureReason = "execution_display_metadata_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(environment.TopologyDigest))
        {
            failureReason = "execution_topology_unavailable";
            return false;
        }

        if (!string.Equals(environment.TopologyDigest, requirements.TopologyDigest, StringComparison.Ordinal))
        {
            failureReason = "execution_topology_mismatch";
            return false;
        }

        if (!TryValidateRegionProjection(requirements, display.PhysicalBounds.Value, out failureReason))
            return false;

        if (!TryValidateOutput(requirements, environment.OutputFileSystem, out failureReason))
            return false;

        failureReason = "";
        return true;
    }

    private static bool TryValidateRegionProjection(
        FixedRegionExecutionEnvironmentRequirements requirements,
        AuthorizedPhysicalRectangle currentDisplayBounds,
        out string failureReason)
    {
        failureReason = "execution_region_invalid";
        try
        {
            var region = requirements.RegionWithinDisplay;
            var projected = requirements.VirtualScreenRegion;
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
        FixedRegionExecutionEnvironmentRequirements requirements,
        StandingLeaseOutputFileSystemSnapshot? output,
        out string failureReason)
    {
        failureReason = "execution_output_environment_unavailable";
        if (output is null)
            return false;

        var expectedDirectory = requirements.NormalizedOutputDirectory;
        var expectedFile = requirements.FrozenOutputFilePath;

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

        if (requirements.OutputConflictPolicy != AuthorizedOutputConflictPolicy.FailIfExists)
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

        var required = RecordingPreflightChecker.RequiredFreeSpaceBytes(requirements.ReservedDuration);
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
