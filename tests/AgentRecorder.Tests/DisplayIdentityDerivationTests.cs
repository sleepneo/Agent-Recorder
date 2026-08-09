using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class DisplayIdentityDerivationTests : IDisposable
{
    private readonly string? _oldTestMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");

    public void Dispose()
    {
        SystemQuery.SetDisplayTopologyProvider(null);
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _oldTestMode);
    }

    [Fact]
    public void SameTargetIdentity_IsStableAcrossSourceEnumerationOrderAndPublicOrdinal()
    {
        var first = DisplayIdentityDeriver.Resolve("\\\\.\\DISPLAY1", new[]
        {
            new DisplayTargetMapping("\\\\.\\DISPLAY1", "target-monitor-A")
        });
        var second = DisplayIdentityDeriver.Resolve("\\\\.\\DISPLAY9", new[]
        {
            new DisplayTargetMapping("\\\\.\\DISPLAY9", "TARGET-MONITOR-A")
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Resolved, first.Status);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual("display_1", first.Fingerprint);
    }

    [Fact]
    public void DifferentTargetIdentity_DiffersEvenWhenPublicIdAndBoundsWouldMatch()
    {
        var left = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "target-monitor-A")
        });
        var right = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "target-monitor-B")
        });

        Assert.NotEqual(left.Fingerprint, right.Fingerprint);
    }

    [Fact]
    public void CloneTargetSet_IsOrderIndependentButChangesWhenSetChanges()
    {
        var first = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "clone-B"),
            new DisplayTargetMapping("source", "clone-A")
        });
        var reordered = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "clone-A"),
            new DisplayTargetMapping("source", "clone-B")
        });
        var changed = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "clone-A"),
            new DisplayTargetMapping("source", "clone-C")
        });

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void MissingDuplicateAndConflictingMappings_NeverCreateFakeIdentity()
    {
        var missing = DisplayIdentityDeriver.Resolve("source", Array.Empty<DisplayTargetMapping>());
        var duplicate = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "target-A"),
            new DisplayTargetMapping("source", "target-A")
        });
        var conflict = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "target-A"),
            new DisplayTargetMapping("other-source", "target-A")
        });
        var missingTarget = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "")
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Missing, missing.Status);
        Assert.Equal(DisplayIdentityResolutionStatus.Ambiguous, duplicate.Status);
        Assert.Equal(DisplayIdentityResolutionStatus.Conflict, conflict.Status);
        Assert.Equal(DisplayIdentityResolutionStatus.Unresolved, missingTarget.Status);
        Assert.All(new[] { missing, duplicate, conflict, missingTarget }, result =>
            Assert.Null(result.Fingerprint));
    }

    [Fact]
    public void ResolvedFingerprint_IsFixedFormatAndDoesNotExposeRawTargetPath()
    {
        const string rawPath = "\\\\?\\DISPLAY#MONITOR_RAW_PATH#{A1B2C3}#7&123";
        var result = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", rawPath)
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Resolved, result.Status);
        Assert.True(DisplayIdentityDeriver.IsFixedFormat(result.Fingerprint));
        Assert.Equal(DisplayIdentityDeriver.FingerprintLength, result.Fingerprint!.Length);
        Assert.StartsWith(DisplayIdentityDeriver.FingerprintPrefix, result.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPath, result.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPath, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableActiveInUseTarget_ResolvesNormally()
    {
        var result = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping(
                "source",
                "target-A",
                PathActive: true,
                TargetAvailable: true,
                TargetInUse: true)
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Resolved, result.Status);
        Assert.True(DisplayIdentityDeriver.IsFixedFormat(result.Fingerprint));
    }

    [Fact]
    public void UnavailableTarget_NeverRetainsFingerprint()
    {
        var result = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "target-A", TargetAvailable: false)
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Unavailable, result.Status);
        Assert.Null(result.Fingerprint);
    }

    [Fact]
    public void OneUnavailableCloneTarget_MakesWholeSourceUnavailable()
    {
        var result = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "clone-A"),
            new DisplayTargetMapping("source", "clone-B", TargetAvailable: false)
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Unavailable, result.Status);
        Assert.Null(result.Fingerprint);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InactiveOrNotInUseTarget_IsUnavailable(bool pathActive, bool targetInUse)
    {
        var result = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping(
                "source",
                "target-A",
                PathActive: pathActive,
                TargetInUse: targetInUse)
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Unavailable, result.Status);
        Assert.Null(result.Fingerprint);
    }

    [Fact]
    public void DeviceInfoFailure_IsUnavailableEvenWhenOtherFieldsLookValid()
    {
        var result = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping(
                "source",
                "target-A",
                SourceDeviceInfoAvailable: false)
        });

        Assert.Equal(DisplayIdentityResolutionStatus.Unavailable, result.Status);
        Assert.Null(result.Fingerprint);
        Assert.DoesNotContain("target-A", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDisplayConfigTypes_MatchWindowsSdkAbi()
    {
        Assert.Equal(48, Marshal.SizeOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>());
        Assert.Equal(72, Marshal.SizeOf<SystemQuery.DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(16, Marshal.OffsetOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>("outputTechnology").ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>("rotation").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>("scaling").ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>("refreshRate").ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>("scanLineOrdering").ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>("targetAvailable").ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<SystemQuery.DISPLAYCONFIG_PATH_TARGET_INFO>("statusFlags").ToInt32());
        Assert.Equal(64, SystemQuery.DISPLAYCONFIG_MODE_INFO_ABI_SIZE);
    }

    [Fact]
    public void ProductionTopologyProvider_PreservesPublicIdBoundsAndIdentityInOneSnapshot()
    {
        var fingerprint = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "target-A")
        }).Fingerprint!;
        SystemQuery.SetDisplayTopologyProvider(() => new List<SystemQuery.DisplayTopologyInfo>
        {
            new("display_2", "Display 2", false, new SystemQuery.Bounds(-1920, 0, 1920, 1080), 1.0,
                fingerprint, DisplayIdentityResolutionStatus.Resolved)
        });

        var snapshot = Assert.Single(SystemQueryDisplayTopologyProvider.Instance.GetCurrentDisplays());

        Assert.Equal("display_2", snapshot.PublicId);
        Assert.Equal(fingerprint, snapshot.StableIdentity);
        Assert.Equal(DisplayIdentityResolutionStatus.Resolved, snapshot.IdentityStatus);
        Assert.Equal(new CapturePlanBounds(-1920, 0, 1920, 1080), snapshot.Bounds);
    }

    [Fact]
    public void ConfigParser_ResolvesPublicIdAndFreezesInternalIdentityFromSameTopologySnapshot()
    {
        Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", null);
        var fingerprint = DisplayIdentityDeriver.Resolve("source", new[]
        {
            new DisplayTargetMapping("source", "target-A")
        }).Fingerprint!;
        SystemQuery.SetDisplayTopologyProvider(() => new List<SystemQuery.DisplayTopologyInfo>
        {
            new("display_7", "Display 7", false, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.0,
                fingerprint, DisplayIdentityResolutionStatus.Resolved)
        });

        var config = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "region",
                ["display_id"] = "display_7",
                ["bounds"] = new JsonObject
                {
                    ["x"] = 100, ["y"] = 100, ["width"] = 640, ["height"] = 480
                }
            },
            ["stop_condition"] = new JsonObject { ["type"] = "duration", ["seconds"] = 5 }
        };

        var recording = ConfigParser.Build(config, "identity-test", out _);

        Assert.Equal("display_7", recording.Config.DisplayId);
        Assert.Equal(fingerprint, recording.Config.DisplayStableIdentity);
        Assert.Equal(DisplayIdentityResolutionStatus.Resolved, recording.Config.DisplayIdentityStatus);
        Assert.DoesNotContain("target-A", JsonSerializer.Serialize(recording.Config));
    }
}
