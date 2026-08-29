using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public sealed class DisplayNumberAlignmentTests : IDisposable
{
    public void Dispose()
    {
        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetDisplayTopologyProvider(null);
        SystemQuery.SetDisplayMonitorEntriesProvider(null);
        SystemQuery.SetDisplayDetailProvider(null);
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", 1)]
    [InlineData(@"\\.\DISPLAY2", 2)]
    [InlineData(@"\\.\display123456", 123456)]
    public void GdiDeviceNameParser_AcceptsCompletePositiveNumbers(string deviceName, int expected)
    {
        Assert.True(WindowsDisplayNumberParser.TryParse(deviceName, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"DISPLAY1")]
    [InlineData(@"\\.\DISPLAY")]
    [InlineData(@"\\.\DISPLAY0")]
    [InlineData(@"\\.\DISPLAY-1")]
    [InlineData(@"\\.\DISPLAY+1")]
    [InlineData(@"\\.\DISPLAY1x")]
    [InlineData(@"\\.\DISPLAY1 ")]
    [InlineData(@"\\.\DISPLAY 1")]
    [InlineData(@"\\.\DISPLAY999999999999999999999999")]
    [InlineData(@"\\.\DISPLAYX1")]
    public void GdiDeviceNameParser_RejectsIncompleteApproximateOrUnsafeValues(string? deviceName)
    {
        Assert.False(WindowsDisplayNumberParser.TryParse(deviceName, out var number));
        Assert.Equal(0, number);
    }

    [Fact]
    public void SameSnapshot_PreservesApiIdsButUsesWindowsNumbersForNamesAcrossDisplayPaths()
    {
        var displays = new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "wrong ordinal", true, new(0, 0, 1920, 1080), 1.0, 2),
            new("display_2", "wrong ordinal", false, new(1920, 0, 1920, 1080), 1.0, 3),
            new("display_3", "wrong ordinal", false, new(-1920, 0, 1920, 1080), 1.0, 1)
        };
        SystemQuery.SetDisplayProvider(() => displays);

        var publicDisplays = SystemQuery.EnumDisplays();
        var topology = SystemQuery.EnumDisplayTopology();
        var details = SystemQuery.EnumDisplayDetails();

        Assert.Equal(
            new[] { ("display_1", 2, "Display 2"), ("display_2", 3, "Display 3"), ("display_3", 1, "Display 1") },
            publicDisplays.Select(display => (display.id, display.windows_display_number!.Value, display.name)));
        Assert.Equal(
            publicDisplays.Select(display => (display.id, display.windows_display_number, display.name)),
            topology.Select(display => (display.id, display.windows_display_number, display.name)));
        Assert.Equal(
            publicDisplays.Select(display => (display.id, display.windows_display_number, display.name)),
            details.Select(display => (display.id, display.windows_display_number, display.name)));
    }

    [Fact]
    public void ProductionMonitorSnapshot_PreservesDpiScaleAcrossDisplayTopologyAndDetailPaths()
    {
        SystemQuery.SetDisplayMonitorEntriesProvider(() => new List<SystemQuery.DisplayMonitorEntry>
        {
            new("display_1", @"\\.\DISPLAY2", true, new(0, 0, 1920, 1080), 1.75, 168, 168, IntPtr.Zero, 2),
            new("display_2", @"\\.\DISPLAY1", false, new(1920, 0, 1920, 1080), 1.25, 120, 120, IntPtr.Zero, 1)
        });

        var publicDisplays = SystemQuery.EnumDisplays();
        var topology = SystemQuery.EnumDisplayTopology();
        var details = SystemQuery.EnumDisplayDetails();

        Assert.Equal(new[] { 1.75, 1.25 }, publicDisplays.Select(display => display.scale_factor));
        Assert.Equal(publicDisplays.Select(display => display.scale_factor), topology.Select(display => display.scale_factor));
        Assert.Equal(publicDisplays.Select(display => display.scale_factor), details.Select(display => display.scale_factor));
    }

    [Fact]
    public void InjectedDisplayTopologyAndDetailProviders_PreserveCallerScaleFactor()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new(0, 0, 100, 100), 1.25)
        });
        Assert.Equal(1.25, SystemQuery.EnumDisplays()[0].scale_factor);
        Assert.Equal(1.25, SystemQuery.EnumDisplayTopology()[0].scale_factor);
        Assert.Equal(1.25, SystemQuery.EnumDisplayDetails()[0].scale_factor);

        SystemQuery.SetDisplayProvider(null);
        SystemQuery.SetDisplayTopologyProvider(() => new List<SystemQuery.DisplayTopologyInfo>
        {
            new("display_1", "Display 1", true, new(0, 0, 100, 100), 1.5,
                null, DisplayIdentityResolutionStatus.Unresolved)
        });
        Assert.Equal(1.5, SystemQuery.EnumDisplays()[0].scale_factor);
        Assert.Equal(1.5, SystemQuery.EnumDisplayTopology()[0].scale_factor);
        Assert.Equal(1.5, SystemQuery.EnumDisplayDetails()[0].scale_factor);

        SystemQuery.SetDisplayTopologyProvider(null);
        SystemQuery.SetDisplayDetailProvider(() => new List<SystemQuery.DisplayDetail>
        {
            new("display_1", "Display 1", true, new(0, 0, 100, 100), 1.75,
                168, 168, IntPtr.Zero)
        });
        Assert.Equal(1.75, SystemQuery.EnumDisplays()[0].scale_factor);
        Assert.Equal(1.75, SystemQuery.EnumDisplayDetails()[0].scale_factor);
    }

    [Fact]
    public void DuplicateWindowsNumbers_FailClosedAndNeverCreateDuplicateUserLabels()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 2", true, new(0, 0, 1920, 1080), 1.0, 2),
            new("display_2", "Display 2", false, new(1920, 0, 1920, 1080), 1.0, 2),
            new("display_3", "Panel", false, new(-1920, 0, 1920, 1080), 1.0)
        });

        var result = SystemQuery.EnumDisplays();

        Assert.Null(result[0].windows_display_number);
        Assert.Null(result[1].windows_display_number);
        Assert.Equal("Display (API display_1)", result[0].name);
        Assert.Equal("Display (API display_2)", result[1].name);
        Assert.Equal(3, result.Select(display => display.name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void MissingWindowsNumber_DoesNotReuseApiOrdinalAsUserVisibleName()
    {
        SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
        {
            new("display_1", "Display 1", true, new(0, 0, 1920, 1080), 1.0),
            new("display_2", "Panel A", false, new(1920, 0, 1920, 1080), 1.0)
        });

        var result = SystemQuery.EnumDisplays();

        Assert.All(result, display => Assert.Null(display.windows_display_number));
        Assert.Equal("Display (API display_1)", result[0].name);
        Assert.Equal("Panel A", result[1].name);
    }

    [Fact]
    public void ConfigParser_UsesAlignedNameForConfirmationButKeepsApiIdForCapture()
    {
        SystemQuery.SetDisplayTopologyProvider(() => new List<SystemQuery.DisplayTopologyInfo>
        {
            new("display_1", "stale ordinal", true, new(0, 0, 1920, 1080), 1.0,
                null, DisplayIdentityResolutionStatus.Unresolved, 2)
        });

        var config = new JsonObject
        {
            ["source"] = new JsonObject
            {
                ["type"] = "display",
                ["display_id"] = "display_1"
            },
            ["stop_condition"] = new JsonObject
            {
                ["type"] = "duration",
                ["seconds"] = 1
            }
        };

        var recording = ConfigParser.Build(config, "display-number-test", out var summary);

        Assert.Equal("display_1", recording.Config.DisplayId);
        Assert.Equal("Display 2", recording.SourceTitle);
        Assert.Contains("Display 2", summary.Source, StringComparison.Ordinal);
    }
}
