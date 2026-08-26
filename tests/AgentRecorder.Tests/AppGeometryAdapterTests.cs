using System;
using System.Collections.Generic;
using System.Drawing;
using AgentRecorder.App;
using AgentRecorder.UI.Geometry;
using AgentRecorder.Windows;
using Xunit;
using AppRegionSelectionGeometry = AgentRecorder.App.RegionSelectionGeometry;
using AppStopControlGeometry = AgentRecorder.App.RecordingStopControlGeometry;
using SharedRegionSelectionGeometry = AgentRecorder.UI.Geometry.RegionSelectionGeometry;

namespace AgentRecorder.Tests;

/// <summary>
/// Small mapping regressions for the App compatibility facades. The larger
/// algorithm suites call the shared implementation directly.
/// </summary>
public sealed class AppGeometryAdapterTests
{
    [Fact]
    public void RegionFacade_MapsSystemQueryDisplayAndWindowDtos_ToSharedResults()
    {
        var form = new Rectangle(-1920, -200, 3840, 2160);
        var display = new SystemQuery.DisplayInfo(
            "display_left", "Left", false, new SystemQuery.Bounds(-1920, -200, 1920, 1080), 1.25);
        var window = new SystemQuery.WindowInfo(
            "window_1", "Editor", "editor.exe", 123, false, false,
            new SystemQuery.Bounds(-1700, 0, 500, 400));

        var sharedDisplay = new GeometryDisplay("display_left", new Rectangle(-1920, -200, 1920, 1080));
        var sharedWindow = new GeometryWindow("window_1", new Rectangle(-1700, 0, 500, 400), false, true);

        Assert.Equal(
            SharedRegionSelectionGeometry.FindDisplayId(new Rectangle(-1700, -100, 400, 300), new[] { sharedDisplay }),
            AppRegionSelectionGeometry.FindDisplayId(new Rectangle(-1700, -100, 400, 300), new[] { display }));
        Assert.Equal(
            SharedRegionSelectionGeometry.ComputeWindowPickBounds(form, sharedWindow),
            AppRegionSelectionGeometry.ComputeWindowPickBounds(form, window));
        Assert.Equal(
            SharedRegionSelectionGeometry.GenerateSnapTargets(form, new[] { sharedDisplay }, new[] { sharedWindow }),
            AppRegionSelectionGeometry.GenerateSnapTargets(form, new[] { display }, new[] { window }));
    }

    [Fact]
    public void StopFacade_MapsRecordingBoundsAndVisibilityMode_ToSharedResult()
    {
        var screen = new Rectangle(-1920, -200, 3840, 2160);
        var inner = new RecordingIndicatorBounds(-1500, 100, 600, 400);
        var parent = new RecordingIndicatorBounds(-1800, -50, 1600, 1000);
        var size = new Size(76, 28);

        var appResult = AppStopControlGeometry.ComputeBounds(
            inner, size, "inner", screen, parent, CaptureVisibilityMode.ParentVisible);
        var sharedResult = StopControlGeometry.ComputeBounds(
            new Rectangle(inner.X, inner.Y, inner.Width, inner.Height), size, "inner", screen,
            new Rectangle(parent.X, parent.Y, parent.Width, parent.Height), StopControlVisibilityMode.ParentVisible);

        Assert.Equal(sharedResult, appResult);

        var appExcluded = AppStopControlGeometry.ComputeBounds(
            inner, size, null, screen, null, CaptureVisibilityMode.ExcludeFromCapture);
        var sharedExcluded = StopControlGeometry.ComputeBounds(
            new Rectangle(inner.X, inner.Y, inner.Width, inner.Height), size, null, screen, null,
            StopControlVisibilityMode.ExcludeFromCapture);

        Assert.Equal(sharedExcluded, appExcluded);
    }

    [Fact]
    public void DpiResolver_MapsSystemQueryDisplayDetails_ToSharedResult()
    {
        var details = new List<SystemQuery.DisplayDetail>
        {
            new("left", "Left", false, new SystemQuery.Bounds(-1920, -200, 1920, 1080), 1.25, 120, 120, IntPtr.Zero),
            new("right", "Right", true, new SystemQuery.Bounds(0, 0, 1920, 1080), 1.5, 144, 144, IntPtr.Zero)
        };
        var target = new Rectangle(-1500, -100, 800, 600);

        SystemQuery.SetDisplayDetailProvider(() => details);
        try
        {
            var appResult = new DisplayDpiResolver().Resolve(target);
            var sharedResult = DisplayDpiGeometry.Resolve(target, new[]
            {
                new DisplayDpiCandidate("left", new Rectangle(-1920, -200, 1920, 1080), 120, 120),
                new DisplayDpiCandidate("right", new Rectangle(0, 0, 1920, 1080), 144, 144)
            });

            Assert.Equal(sharedResult.MonitorId, appResult.MonitorId);
            Assert.Equal(sharedResult.MonitorBounds, appResult.MonitorBounds);
            Assert.Equal(sharedResult.DpiX, appResult.DpiX);
            Assert.Equal(sharedResult.DpiY, appResult.DpiY);
            Assert.Equal(sharedResult.Scale, appResult.Scale);
            Assert.Equal(sharedResult.IsFallback, appResult.IsFallback);
            Assert.Equal(sharedResult.FallbackReason, appResult.FallbackReason);
        }
        finally
        {
            SystemQuery.SetDisplayDetailProvider(null);
        }
    }
}
