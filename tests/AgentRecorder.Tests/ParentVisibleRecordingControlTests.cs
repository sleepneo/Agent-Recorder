using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Windows;
using SharedStopControlGeometry = AgentRecorder.UI.Geometry.StopControlGeometry;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Headless tests for the parent-visible inner recording control mode. These tests verify
/// geometry constraints, capture-visibility mode
/// decisions, production parent resolution, combined planning, and display-affinity contracts
/// without calling <see cref="Form.Show()"/> or popping real recording UI.
/// </summary>
public class ParentVisibleRecordingControlTests
{
    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new TargetInvocationException(ex);
    }

    private static RecordingUiPresentation MakeRecording(
        (int x, int y, int w, int h) bounds,
        string? nestedRole = null,
        string? parentRecordingId = null,
        string? nestedSessionId = null)
    {
        return new RecordingUiPresentation
        {
            RecordingId = "rec_" + Guid.NewGuid().ToString("N")[..12],
            State = RecordingUiState.Recording,
            SourceType = "region",
            CaptureBounds = new RecordingUiBounds(bounds.x, bounds.y, bounds.w, bounds.h),
            StartedAtUtc = DateTime.UtcNow,
            NestedRole = nestedRole,
            ParentRecordingId = parentRecordingId,
            NestedSessionId = nestedSessionId
        };
    }

    private static WindowDisplayAffinity FakeAffinity(bool result)
    {
        return new WindowDisplayAffinity((hWnd, mode) => result, null);
    }

    private static Size DefaultLabelSize => new(80, 20);

    private static void SetActualWindowDpi(Form form, int dpi)
    {
        var field = form.GetType().GetField("_actualWindowDpi", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(form, dpi);
    }

    private sealed class ForcedDpiResolver : IDisplayDpiResolver
    {
        private readonly DisplayDpiInfo _dpi;
        public ForcedDpiResolver(DisplayDpiInfo dpi) => _dpi = dpi;
        public DisplayDpiInfo Resolve(Rectangle bounds) => _dpi;
    }

    private static Rectangle InnerRect(RecordingIndicatorPresentation plan)
    {
        var b = plan.InnerCaptureBounds;
        return new Rectangle(b.X, b.Y, b.Width, b.Height);
    }

    private static Rectangle ParentRect(RecordingIndicatorPresentation plan)
    {
        var b = plan.ParentCaptureBounds ?? throw new InvalidOperationException("No parent bounds");
        return new Rectangle(b.X, b.Y, b.Width, b.Height);
    }

    // =====================================================================
    // Production parent resolution
    // =====================================================================

    [Fact]
    public void ResolveActiveParentForUi_InnerWithActiveOuter_ReturnsParent()
    {
        var outer = MakeRecording((0, 0, 1000, 800), "outer", nestedSessionId: "session-a");
        var inner = MakeRecording((200, 200, 400, 300), "inner", outer.RecordingId, "session-a");
        var active = new Dictionary<string, RecordingUiPresentation> { [outer.RecordingId] = outer };

        var resolved = TrayContext.ResolveActiveParentForUi(inner, active);

        Assert.Same(outer, resolved);
    }

    [Fact]
    public void ResolveActiveParentForUi_RegularRecording_ReturnsNull()
    {
        var rec = MakeRecording((100, 100, 800, 600));
        var active = new Dictionary<string, RecordingUiPresentation> { [rec.RecordingId] = rec };

        Assert.Null(TrayContext.ResolveActiveParentForUi(rec, active));
    }

    [Fact]
    public void ResolveActiveParentForUi_ParentIdMismatch_ReturnsNull()
    {
        var outer = MakeRecording((0, 0, 1000, 800), "outer", nestedSessionId: "session-a");
        var inner = MakeRecording((200, 200, 400, 300), "inner", "wrong-parent-id", "session-a");
        var active = new Dictionary<string, RecordingUiPresentation> { [outer.RecordingId] = outer };

        Assert.Null(TrayContext.ResolveActiveParentForUi(inner, active));
    }

    [Fact]
    public void ResolveActiveParentForUi_ParentNotOuter_ReturnsNull()
    {
        var notOuter = MakeRecording((0, 0, 1000, 800), "inner", nestedSessionId: "session-a");
        var inner = MakeRecording((200, 200, 400, 300), "inner", notOuter.RecordingId, "session-a");
        var active = new Dictionary<string, RecordingUiPresentation> { [notOuter.RecordingId] = notOuter };

        Assert.Null(TrayContext.ResolveActiveParentForUi(inner, active));
    }

    [Theory]
    [InlineData("session-a", "session-b")]
    [InlineData("session-a", null)]
    [InlineData(null, "session-a")]
    public void ResolveActiveParentForUi_SessionMismatch_ReturnsNull(string? innerSession, string? outerSession)
    {
        var outer = MakeRecording((0, 0, 1000, 800), "outer", nestedSessionId: outerSession);
        var inner = MakeRecording((200, 200, 400, 300), "inner", outer.RecordingId, innerSession);
        var active = new Dictionary<string, RecordingUiPresentation> { [outer.RecordingId] = outer };

        Assert.Null(TrayContext.ResolveActiveParentForUi(inner, active));
    }

    [Fact]
    public void ResolveActiveParentForUi_BothSessionsNull_Matches()
    {
        var outer = MakeRecording((0, 0, 1000, 800), "outer");
        var inner = MakeRecording((200, 200, 400, 300), "inner", outer.RecordingId);
        var active = new Dictionary<string, RecordingUiPresentation> { [outer.RecordingId] = outer };

        var resolved = TrayContext.ResolveActiveParentForUi(inner, active);
        Assert.Same(outer, resolved);
    }

    [Fact]
    public void ResolveActiveParentForUi_MissingParent_ReturnsNull()
    {
        var inner = MakeRecording((200, 200, 400, 300), "inner", "missing-id", "session-a");
        var active = new Dictionary<string, RecordingUiPresentation>();

        Assert.Null(TrayContext.ResolveActiveParentForUi(inner, active));
    }

    // =====================================================================
    // Presentation plan mode decisions
    // =====================================================================

    [Fact]
    public void ComputePresentationPlan_Regular_ExcludeAndRequestsAffinity()
    {
        var vs = SystemInformation.VirtualScreen;
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 800, 600));
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, null, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Null(plan.FallbackReason);
        Assert.Equal(capture, plan.WindowBounds);
    }

    [Fact]
    public void ComputePresentationPlan_Outer_ExcludeAndRequestsAffinity()
    {
        var vs = SystemInformation.VirtualScreen;
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 800, 600), "outer");
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);
        var parent = new RecordingIndicatorBounds(vs.X, vs.Y, 1920, 1080);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, null, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Null(plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_ParentMissing_Fallbacks()
    {
        var vs = SystemInformation.VirtualScreen;
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 400, 300), "inner", "p1", "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, null, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Equal("parent_missing", plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_ParentRecordingIdMissing_Fallbacks()
    {
        var vs = SystemInformation.VirtualScreen;
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 400, 300), "inner", null, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 400, 300);
        var parentRec = MakeRecording((vs.X, vs.Y, 1920, 1080), "outer", nestedSessionId: "session-a");

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Equal("parent_missing", plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_ParentIdMismatch_Fallbacks()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1920, 1080), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 400, 300), "inner", "wrong-id", "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Equal("parent_id_mismatch", plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_ParentNotOuter_Fallbacks()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1920, 1080), "inner", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 400, 300), "inner", parentRec.RecordingId, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Equal("parent_not_outer", plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_SessionMismatch_Fallbacks()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1920, 1080), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 400, 300), "inner", parentRec.RecordingId, "session-b");
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Equal("session_mismatch", plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_NotContained_Fallbacks()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1920, 1080), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 2000, vs.Y + 100, 400, 300), "inner", parentRec.RecordingId, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 2000, vs.Y + 100, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Equal("inner_not_contained", plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_InsufficientMargin_Fallbacks()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X + 98, vs.Y + 98, 404, 304), "outer", nestedSessionId: "session-a");
        // Parent only 2 px larger than inner on each side, not enough for 4 px border + label.
        var rec = MakeRecording((vs.X + 100, vs.Y + 100, 400, 300), "inner", parentRec.RecordingId, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.Mode);
        Assert.True(plan.DisplayAffinityRequested);
        Assert.Equal("insufficient_margin", plan.FallbackReason);
    }

    [Fact]
    public void ComputePresentationPlan_Inner_ValidParentVisible_DoesNotRequestAffinity()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1000, 800), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 200, vs.Y + 200, 400, 300), "inner", parentRec.RecordingId, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 200, vs.Y + 200, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ParentVisible, plan.Mode);
        Assert.False(plan.DisplayAffinityRequested);
        Assert.Null(plan.FallbackReason);
    }

    // =====================================================================
    // Parent-visible geometry invariants
    // =====================================================================

    [Fact]
    public void ComputePresentationPlan_ParentVisible_BordersAndLabelAreOutsideInner()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1000, 800), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 200, vs.Y + 200, 400, 300), "inner", parentRec.RecordingId, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 200, vs.Y + 200, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ParentVisible, plan.Mode);

        var innerRect = InnerRect(plan);
        foreach (var border in plan.BorderRectangles)
        {
            Assert.False(border.IntersectsWith(innerRect),
                $"Border {border} intersects the inner capture rectangle");
            Assert.True(ParentRect(plan).Contains(border),
                $"Border {border} is not contained by the parent capture rectangle");
            Assert.True(vs.Contains(border),
                $"Border {border} is not contained by the virtual screen");
        }

        Assert.False(plan.LabelBounds.IntersectsWith(innerRect),
            "Label intersects the inner capture rectangle");
        Assert.True(ParentRect(plan).Contains(plan.LabelBounds),
            "Label is not contained by the parent capture rectangle");
        Assert.True(vs.Contains(plan.LabelBounds),
            "Label is not contained by the virtual screen");
    }

    [Fact]
    public void ComputePresentationPlan_ParentVisible_LabelUsesFullMeasuredSize()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1000, 800), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 200, vs.Y + 200, 400, 300), "inner", parentRec.RecordingId, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 200, vs.Y + 200, 400, 300);
        var labelSize = new Size(120, 24);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, labelSize, vs);

        Assert.Equal(CaptureVisibilityMode.ParentVisible, plan.Mode);
        Assert.True(plan.LabelBounds.Width >= labelSize.Width,
            "Label width was cropped in parent-visible mode");
        Assert.True(plan.LabelBounds.Height >= labelSize.Height,
            "Label height was cropped in parent-visible mode");
    }

    [Fact]
    public void ComputePresentationPlan_ParentVisible_WindowBoundsContainsAllColoredPixels()
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((vs.X, vs.Y, 1000, 800), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((vs.X + 200, vs.Y + 200, 400, 300), "inner", parentRec.RecordingId, "session-a");
        var capture = new RecordingIndicatorBounds(vs.X + 200, vs.Y + 200, 400, 300);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        var window = new Rectangle(plan.WindowBounds.X, plan.WindowBounds.Y, plan.WindowBounds.Width, plan.WindowBounds.Height);
        foreach (var border in plan.BorderRectangles)
        {
            Assert.True(window.Contains(border),
                $"Window {window} does not contain border {border}");
        }
        Assert.True(window.Contains(plan.LabelBounds),
            $"Window {window} does not contain label {plan.LabelBounds}");
    }

    [Theory]
    [InlineData(0, 0, 1000, 800, 200, 200, 400, 300)]
    [InlineData(-1920, -200, 3200, 1280, -1400, 0, 400, 300)]
    [InlineData(0, 0, 500, 500, 50, 50, 300, 300)]
    public void ComputePresentationPlan_ParentVisible_VariousBounds_GeometryValid(
        int px, int py, int pw, int ph,
        int ix, int iy, int iw, int ih)
    {
        var vs = SystemInformation.VirtualScreen;
        var parentRec = MakeRecording((px, py, pw, ph), "outer", nestedSessionId: "session-a");
        var rec = MakeRecording((ix, iy, iw, ih), "inner", parentRec.RecordingId, "session-a");
        var parent = new RecordingIndicatorBounds(px, py, pw, ph);
        var capture = new RecordingIndicatorBounds(ix, iy, iw, ih);

        var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
            rec, capture, parentRec, DefaultLabelSize, vs);

        if (plan.Mode != CaptureVisibilityMode.ParentVisible)
            return; // Some configurations may legitimately fall back.

        var innerRect = InnerRect(plan);
        foreach (var border in plan.BorderRectangles)
        {
            Assert.False(border.IntersectsWith(innerRect));
            Assert.True(ParentRect(plan).Contains(border));
            Assert.True(vs.Contains(border));
        }
        Assert.False(plan.LabelBounds.IntersectsWith(innerRect));
        Assert.True(ParentRect(plan).Contains(plan.LabelBounds));
        Assert.True(vs.Contains(plan.LabelBounds));
    }

    // =====================================================================
    // Stop-control geometry in parent-visible mode
    // =====================================================================

    [Fact]
    public void ComputeBounds_ParentVisible_PlacesButtonOutsideInnerInsideParent()
    {
        var vs = new Rectangle(0, 0, 3200, 1610);
        var inner = new Rectangle(vs.X + 200, vs.Y + 200, 400, 300);
        var parent = new Rectangle(vs.X, vs.Y, 1000, 800);
        var controlSize = new Size(SharedStopControlGeometry.DefaultButtonWidth, SharedStopControlGeometry.DefaultButtonHeight);

        var bounds = SharedStopControlGeometry.ComputeBounds(
            inner, controlSize, "inner", vs, parent, StopControlVisibilityMode.ParentVisible);

        var rect = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var innerRect = new Rectangle(inner.X, inner.Y, inner.Width, inner.Height);
        var parentRect = new Rectangle(parent.X, parent.Y, parent.Width, parent.Height);

        Assert.False(rect.IntersectsWith(innerRect),
            "Stop button intersects the inner capture rectangle");
        Assert.True(parentRect.Contains(rect),
            "Stop button is not contained by the parent capture rectangle");
        Assert.True(vs.Contains(rect),
            "Stop button is not contained by the virtual screen");
    }

    [Fact]
    public void ResolveCollision_ParentVisible_MovesOutOfForbiddenZone()
    {
        var vs = new Rectangle(0, 0, 3200, 1610);
        var inner = new Rectangle(vs.X + 200, vs.Y + 200, 400, 300);
        var parent = new Rectangle(vs.X, vs.Y, 1000, 800);
        var preferred = new RecordingStopControlBounds(inner.X + 10, inner.Y + 10, 76, 28);
        var controlSize = new Size(76, 28);

        var result = SharedStopControlGeometry.ResolveCollision(
            preferred, controlSize, vs, Array.Empty<RecordingStopControlBounds>(), inner, parent);

        Assert.NotNull(result);
        var rect = new Rectangle(result!.X, result.Y, result.Width, result.Height);
        Assert.False(rect.IntersectsWith(inner),
            "Resolved stop button intersects the forbidden inner zone");
        Assert.True(parent.Contains(rect),
            "Resolved stop button is not contained by the allowed parent zone");
        Assert.True(vs.Contains(rect),
            "Resolved stop button is not contained by the virtual screen");
    }

    [Fact]
    public void ResolveCollision_ParentVisible_AvoidsOccupiedAndKeepsConstraints()
    {
        var vs = new Rectangle(0, 0, 3200, 1610);
        var inner = new Rectangle(vs.X + 200, vs.Y + 200, 400, 300);
        var parent = new Rectangle(vs.X, vs.Y, 1000, 800);
        var occupied = new[]
        {
            new RecordingStopControlBounds(inner.X + inner.Width + 4, inner.Y, 76, 28)
        };
        var preferred = occupied[0];
        var controlSize = new Size(76, 28);

        var result = SharedStopControlGeometry.ResolveCollision(
            preferred, controlSize, vs, occupied, inner, parent);

        Assert.NotNull(result);
        var rect = new Rectangle(result!.X, result.Y, result.Width, result.Height);
        Assert.False(rect.IntersectsWith(inner),
            "Resolved stop button intersects the forbidden inner zone");
        Assert.True(parent.Contains(rect),
            "Resolved stop button is not contained by the allowed parent zone");
        Assert.All(occupied, o => Assert.False(SharedStopControlGeometry.Intersects(result, o),
            "Resolved stop button intersects an occupied stop control"));
    }

    [Fact]
    public void ResolveCollision_ParentVisible_NoSafePosition_ReturnsNull()
    {
        var vs = new Rectangle(0, 0, 3200, 1610);
        // Parent is only large enough for the inner rectangle, leaving no room for the button.
        var inner = new Rectangle(vs.X + 100, vs.Y + 100, 200, 150);
        var parent = new Rectangle(vs.X + 100, vs.Y + 100, 200, 150);
        var controlSize = new Size(76, 28);
        var preferred = new RecordingStopControlBounds(inner.X, inner.Y, controlSize.Width, controlSize.Height);

        var result = SharedStopControlGeometry.ResolveCollision(
            preferred, controlSize, vs, Array.Empty<RecordingStopControlBounds>(),
            new Rectangle(inner.X, inner.Y, inner.Width, inner.Height),
            new Rectangle(parent.X, parent.Y, parent.Width, parent.Height));

        Assert.Null(result);
    }

    [Fact]
    public void TryResolveCollision_ParentVisible_NoSafePosition_ReturnsFalse()
    {
        var vs = new Rectangle(0, 0, 3200, 1610);
        var inner = new Rectangle(vs.X + 100, vs.Y + 100, 200, 150);
        var parent = new Rectangle(vs.X + 100, vs.Y + 100, 200, 150);
        var controlSize = new Size(76, 28);
        var preferred = new RecordingStopControlBounds(inner.X, inner.Y, controlSize.Width, controlSize.Height);

        bool ok = SharedStopControlGeometry.TryResolveCollision(
            preferred, controlSize, vs, Array.Empty<RecordingStopControlBounds>(),
            inner,
            parent,
            out var result);

        Assert.False(ok);
        Assert.Null(result);
    }

    // =====================================================================
    // RecordingIndicatorForm display-affinity contract
    // =====================================================================

    [Fact]
    public void IndicatorForm_ParentVisible_DoesNotRequestDisplayAffinity()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            var parentRec = MakeRecording((vs.X, vs.Y, 1000, 800), "outer", nestedSessionId: "session-a");
            var rec = MakeRecording((vs.X + 200, vs.Y + 200, 400, 300), "inner", parentRec.RecordingId, "session-a");
            var capture = new RecordingIndicatorBounds(vs.X + 200, vs.Y + 200, 400, 300);
            var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
                rec, capture, parentRec, DefaultLabelSize, vs);

            using var form = new RecordingIndicatorForm(
                "r1", plan, DateTime.UtcNow, 30, "inner", FakeAffinity(true));

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(form.DisplayAffinityAppliedForTests);
            Assert.Null(form.DisplayAffinityErrorForTests);
            Assert.Equal(CaptureVisibilityMode.ParentVisible, form.CaptureVisibilityModeForTests);
        });
    }

    [Fact]
    public void IndicatorForm_ExcludeMode_RequestsDisplayAffinity()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            var rec = MakeRecording((vs.X + 100, vs.Y + 100, 800, 600));
            var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);
            var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
                rec, capture, null, DefaultLabelSize, vs);

            using var form = new RecordingIndicatorForm(
                "r1", plan, DateTime.UtcNow, 30, null, FakeAffinity(true));

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.True(form.DisplayAffinityAppliedForTests);
            Assert.Null(form.DisplayAffinityErrorForTests);
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, form.CaptureVisibilityModeForTests);
        });
    }

    [Fact]
    public void IndicatorForm_ExcludeMode_AffinityFailure_DoesNotThrowAndKeepsWindowUsable()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            var rec = MakeRecording((vs.X + 100, vs.Y + 100, 800, 600), "outer");
            var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);
            var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
                rec, capture, null, DefaultLabelSize, vs);

            using var form = new RecordingIndicatorForm(
                "r1", plan, DateTime.UtcNow, 30, "outer", FakeAffinity(false));

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(form.DisplayAffinityAppliedForTests);
            Assert.Null(form.DisplayAffinityErrorForTests);
            Assert.False(form.IsDisposed);
        });
    }

    [Fact]
    public void IndicatorForm_ExcludeMode_AffinityException_DoesNotThrowAndRecordsError()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            var rec = MakeRecording((vs.X + 100, vs.Y + 100, 800, 600));
            var capture = new RecordingIndicatorBounds(vs.X + 100, vs.Y + 100, 800, 600);
            var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
                rec, capture, null, DefaultLabelSize, vs);

            using var form = new RecordingIndicatorForm(
                "r1", plan, DateTime.UtcNow, 30, null, ThrowingAffinity());

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(form.DisplayAffinityAppliedForTests);
            Assert.NotNull(form.DisplayAffinityErrorForTests);
            Assert.False(form.IsDisposed);
        });
    }

    private static WindowDisplayAffinity ThrowingAffinity()
    {
        return new WindowDisplayAffinity((hWnd, mode) => throw new InvalidOperationException("affinity boom"), null);
    }

    // =====================================================================
    // RecordingStopControlForm display-affinity contract
    // =====================================================================

    [Fact]
    public void StopControlForm_ParentVisible_DoesNotRequestDisplayAffinity()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm(
                "r1",
                new RecordingStopControlBounds(100, 100, 76, 28),
                CaptureVisibilityMode.ParentVisible,
                displayAffinity: FakeAffinity(true));

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(form.DisplayAffinityAppliedForTests);
            Assert.Null(form.DisplayAffinityErrorForTests);
            Assert.Equal(CaptureVisibilityMode.ParentVisible, form.CaptureVisibilityModeForTests);
        });
    }

    [Fact]
    public void StopControlForm_ExcludeMode_RequestsDisplayAffinity()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm(
                "r1",
                new RecordingStopControlBounds(100, 100, 76, 28),
                CaptureVisibilityMode.ExcludeFromCapture,
                displayAffinity: FakeAffinity(true));

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.True(form.DisplayAffinityAppliedForTests);
            Assert.Null(form.DisplayAffinityErrorForTests);
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, form.CaptureVisibilityModeForTests);
        });
    }

    [Fact]
    public void StopControlForm_ExcludeMode_AffinityFailure_DoesNotThrowAndKeepsWindowUsable()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm(
                "r1",
                new RecordingStopControlBounds(100, 100, 76, 28),
                CaptureVisibilityMode.ExcludeFromCapture,
                displayAffinity: FakeAffinity(false));

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(form.DisplayAffinityAppliedForTests);
            Assert.Null(form.DisplayAffinityErrorForTests);
            Assert.True(form.ButtonEnabledForTests);
        });
    }

    [Fact]
    public void StopControlForm_ExcludeMode_AffinityException_DoesNotThrowAndRecordsError()
    {
        RunOnSta(() =>
        {
            using var form = new RecordingStopControlForm(
                "r1",
                new RecordingStopControlBounds(100, 100, 76, 28),
                CaptureVisibilityMode.ExcludeFromCapture,
                displayAffinity: ThrowingAffinity());

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.False(form.DisplayAffinityAppliedForTests);
            Assert.NotNull(form.DisplayAffinityErrorForTests);
            Assert.True(form.ButtonEnabledForTests);
        });
    }

    // =====================================================================
    // Combined control plan (no UI creation)
    // =====================================================================

    [Fact]
    public void ComputeControlPlan_Regular_ExcludeModeAndAffinityRequested()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var rec = MakeRecording((100, 100, 800, 600));

            var plan = mgr.ComputeControlPlan(rec, null, SystemInformation.VirtualScreen);

            Assert.NotNull(plan);
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan!.IndicatorPresentation.Mode);
            Assert.True(plan.IndicatorPresentation.DisplayAffinityRequested);
            Assert.Null(plan.IndicatorPresentation.FallbackReason);
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan.IndicatorPresentation.Mode);
        });
    }

    [Fact]
    public void ComputeControlPlan_ValidInnerParentVisible_DoesNotRequestAffinity()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var parent = MakeRecording((0, 0, 1000, 800), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((200, 200, 400, 300), "inner", parent.RecordingId, "session-a");

            // The parent must be active in the manager so the inner plan sees no occupied stop control.
            var outerPlan = mgr.ComputeControlPlan(parent, null, SystemInformation.VirtualScreen);
            Assert.NotNull(outerPlan);

            var innerPlan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen);

            Assert.NotNull(innerPlan);
            Assert.Equal(CaptureVisibilityMode.ParentVisible, innerPlan!.IndicatorPresentation.Mode);
            Assert.False(innerPlan.IndicatorPresentation.DisplayAffinityRequested);
            Assert.Null(innerPlan.IndicatorPresentation.FallbackReason);
        });
    }

    [Fact]
    public void ComputeControlPlan_InnerNoSafeStopPosition_JointFallbackToExclude()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            // Parent is only slightly larger than inner: the 4 px border and the REC label fit,
            // but the stop button (76x28) does not fit in any margin. This triggers the joint
            // fallback after indicator geometry succeeds.
            var parent = MakeRecording((0, 0, 250, 261), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((30, 30, 200, 200), "inner", parent.RecordingId, "session-a");

            var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen);

            Assert.NotNull(plan);
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, plan!.IndicatorPresentation.Mode);
            Assert.Equal("no_safe_stop_position", plan.IndicatorPresentation.FallbackReason);
            Assert.True(plan.IndicatorPresentation.DisplayAffinityRequested);
            Assert.Equal("no_safe_stop_position", plan.FallbackReason);
        });
    }

    [Fact]
    public void ComputeControlPlan_ParentVisible_StopBoundsSatisfyAllConstraints()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var parent = MakeRecording((0, 0, 1000, 800), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((200, 200, 400, 300), "inner", parent.RecordingId, "session-a");

            var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen);

            Assert.NotNull(plan);
            Assert.Equal(CaptureVisibilityMode.ParentVisible, plan!.IndicatorPresentation.Mode);

            var stopRect = new Rectangle(plan.StopBounds.X, plan.StopBounds.Y, plan.StopBounds.Width, plan.StopBounds.Height);
            var innerRect = new Rectangle(200, 200, 400, 300);
            var parentRect = new Rectangle(0, 0, 1000, 800);
            var vs = SystemInformation.VirtualScreen;

            Assert.False(stopRect.IntersectsWith(innerRect),
                "Stop button intersects inner capture rectangle");
            Assert.True(parentRect.Contains(stopRect),
                "Stop button is not inside parent");
            Assert.True(vs.Contains(stopRect),
                "Stop button is not inside virtual screen");
        });
    }

    [Fact]
    public void ComputeControlPlan_ParentVisible_OccupiedOuterButton_AvoidsOverlap()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var parent = MakeRecording((0, 0, 1000, 800), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((200, 200, 400, 300), "inner", parent.RecordingId, "session-a");

            // Pre-place an outer stop control in the preferred right-side slot.
            var preferred = RecordingStopControlGeometry.ComputeBounds(
                new RecordingIndicatorBounds(200, 200, 400, 300),
                new Size(RecordingStopControlGeometry.DefaultButtonWidth, RecordingStopControlGeometry.DefaultButtonHeight),
                "inner",
                SystemInformation.VirtualScreen,
                new RecordingIndicatorBounds(0, 0, 1000, 800),
                CaptureVisibilityMode.ParentVisible);

            var outerStop = new RecordingStopControlForm(
                "outer-stop",
                preferred,
                CaptureVisibilityMode.ExcludeFromCapture,
                displayAffinity: FakeAffinity(true));

            try
            {
                // Inject the occupied control by reflection so ComputeControlPlan sees it.
                var dict = typeof(RecordingIndicatorManager)
                    .GetField("_stopControls", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(mgr) as Dictionary<string, RecordingStopControlForm>;
                Assert.NotNull(dict);
                dict!["outer-stop"] = outerStop;

                var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen);

                Assert.NotNull(plan);
                if (plan!.IndicatorPresentation.Mode == CaptureVisibilityMode.ParentVisible)
                {
                    Assert.False(RecordingStopControlGeometry.Intersects(plan.StopBounds, preferred),
                        "Inner stop button overlaps the occupied outer button");
                }
                else
                {
                    Assert.Equal("no_safe_stop_position", plan.IndicatorPresentation.FallbackReason);
                }
            }
            finally
            {
                outerStop.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    public void ComputeControlPlan_InjectedDpi_ParentVisibleOrFallback(int dpi)
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var parent = MakeRecording((0, 0, 1200, 900), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((300, 300, 500, 400), "inner", parent.RecordingId, "session-a");

            var dpiInfo = new DisplayDpiInfo("test_display", new Rectangle(0, 0, 1920, 1080), dpi, dpi, dpi / 96f, false, null);
            var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen, dpiInfo);

            Assert.NotNull(plan);
            Assert.Equal(dpi, plan!.DpiInfo.DpiX);

            if (plan.IndicatorPresentation.Mode == CaptureVisibilityMode.ParentVisible)
            {
                var stopRect = new Rectangle(plan.StopBounds.X, plan.StopBounds.Y, plan.StopBounds.Width, plan.StopBounds.Height);
                var innerRect = new Rectangle(300, 300, 500, 400);
                Assert.False(stopRect.IntersectsWith(innerRect));
            }
            else
            {
                Assert.True(plan.IndicatorPresentation.DisplayAffinityRequested);
            }
        });
    }

    [Fact]
    public void ComputeControlPlan_ParentVisible_LabelBoundsNotCropped()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var parent = MakeRecording((0, 0, 1200, 900), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((300, 300, 500, 400), "inner", parent.RecordingId, "session-a");

            var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen);

            Assert.NotNull(plan);
            if (plan!.IndicatorPresentation.Mode == CaptureVisibilityMode.ParentVisible)
            {
                var fullLabelSize = RecordingIndicatorForm.MeasureLabelSize(
                    "inner",
                    inner.DurationSeconds,
                    new Font("Segoe UI", 9, FontStyle.Bold),
                    new Padding(4, 2, 4, 2));
                Assert.True(plan.IndicatorPresentation.LabelBounds.Width >= fullLabelSize.Width);
                Assert.True(plan.IndicatorPresentation.LabelBounds.Height >= fullLabelSize.Height);
            }
        });
    }

    [Fact]
    public void ComputeControlPlan_DpiIncrease_ButtonNoLongerFits_JointFallbackToExclude()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();

            // Deterministic size provider: the button doubles in size at 200 % DPI.
            Size StopSizeProvider(IUiTextProvider text, Font font, DisplayDpiInfo dpi) =>
                new Size(80 * (int)Math.Round(dpi.Scale), 30 * (int)Math.Round(dpi.Scale));

            var mgr = new RecordingIndicatorManager(
                audit,
                _ => { },
                (id, bounds, started, duration, role) =>
                    new RecordingIndicatorForm(id, bounds, started, duration, role),
                (id, bounds, size, dpi) => new RecordingStopControlForm(id, bounds, size, dpi),
                stopControlSizeProvider: StopSizeProvider);

            // Margins are sized so the 96-DPI button (80x30) and label fit, but the
            // 192-DPI button (160x60) does not fit in any margin, forcing a joint fallback.
            // Top/bottom margins are wide enough for the label but kept below 60 px tall so
            // the doubled button height does not fit; left/right margins are kept below 160 px
            // wide so the doubled button width does not fit either.
            var parent = MakeRecording((0, 0, 650, 148), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((100, 54, 500, 40), "inner", parent.RecordingId, "session-a");

            var lowDpi = new DisplayDpiInfo("test", new Rectangle(0, 0, 1920, 1080), 96, 96, 1.0f, false, null);
            var highDpi = new DisplayDpiInfo("test", new Rectangle(0, 0, 1920, 1080), 192, 192, 2.0f, false, null);

            var lowPlan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen, lowDpi);
            var highPlan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen, highDpi);

            Assert.NotNull(lowPlan);
            Assert.NotNull(highPlan);
            Assert.Equal(CaptureVisibilityMode.ParentVisible, lowPlan!.IndicatorPresentation.Mode);
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, highPlan!.IndicatorPresentation.Mode);
            Assert.Equal("no_safe_stop_position", highPlan.IndicatorPresentation.FallbackReason);
        });
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    public void ComputeControlPlan_InjectedDpi_LabelSizeMatchesDpiAwareMeasurement(int dpi)
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            var mgr = new RecordingIndicatorManager(audit);
            var parent = MakeRecording((0, 0, 1200, 900), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((300, 300, 500, 400), "inner", parent.RecordingId, "session-a");

            var dpiInfo = new DisplayDpiInfo("test_display", new Rectangle(0, 0, 1920, 1080), dpi, dpi, dpi / 96f, false, null);
            var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen, dpiInfo);

            Assert.NotNull(plan);
            Assert.Equal(dpi, plan!.DpiInfo.DpiX);

            if (plan.IndicatorPresentation.Mode == CaptureVisibilityMode.ParentVisible)
            {
                using var font = new Font("Segoe UI", 9, FontStyle.Bold);
                var expectedSize = RecordingIndicatorForm.MeasureLabelSize(
                    "inner", inner.DurationSeconds, font, new Padding(4, 2, 4, 2), dpiInfo);
                Assert.Equal(expectedSize.Width, plan.IndicatorPresentation.LabelBounds.Width);
                Assert.Equal(expectedSize.Height, plan.IndicatorPresentation.LabelBounds.Height);
            }
            else
            {
                Assert.True(plan.IndicatorPresentation.DisplayAffinityRequested);
            }
        });
    }

    [Fact]
    public void ComputeControlPlan_HighDpi_LabelNoLongerFits_JointFallbackToExclude()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();

            // Use a tiny deterministic stop button so this test isolates the label sizing
            // behaviour. The production stop-control sizing is covered by the DPI matrix test
            // above and by RecordingStopControlTests.
            Size StopSizeProvider(IUiTextProvider text, Font font, DisplayDpiInfo dpi) =>
                new Size(40, 20);

            var mgr = new RecordingIndicatorManager(
                audit,
                _ => { },
                (id, bounds, started, duration, role) =>
                    new RecordingIndicatorForm(id, bounds, started, duration, role),
                (id, bounds, size, dpi) => new RecordingStopControlForm(id, bounds, size, dpi),
                stopControlSizeProvider: StopSizeProvider);

            // At 96 DPI the label fits in the top margin (36 px); at 192 DPI it is too tall
            // and too wide for every margin, so the whole plan falls back.
            var parent = MakeRecording((0, 0, 460, 274), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((30, 40, 400, 200), "inner", parent.RecordingId, "session-a");

            var lowDpi = new DisplayDpiInfo("test", new Rectangle(0, 0, 1920, 1080), 96, 96, 1.0f, false, null);
            var highDpi = new DisplayDpiInfo("test", new Rectangle(0, 0, 1920, 1080), 192, 192, 2.0f, false, null);

            var lowPlan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen, lowDpi);
            var highPlan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen, highDpi);

            Assert.NotNull(lowPlan);
            Assert.NotNull(highPlan);
            Assert.Equal(CaptureVisibilityMode.ParentVisible, lowPlan!.IndicatorPresentation.Mode);
            Assert.Equal(CaptureVisibilityMode.ExcludeFromCapture, highPlan!.IndicatorPresentation.Mode);
            Assert.Equal("insufficient_margin", highPlan.IndicatorPresentation.FallbackReason);
        });
    }

    [Fact]
    public void IndicatorForm_ParentVisible_AutoScaleNoneAndLabelBoundsMatchPlan()
    {
        RunOnSta(() =>
        {
            var vs = SystemInformation.VirtualScreen;
            var parentRec = MakeRecording((vs.X, vs.Y, 1000, 800), "outer", nestedSessionId: "session-a");
            var rec = MakeRecording((vs.X + 200, vs.Y + 200, 400, 300), "inner", parentRec.RecordingId, "session-a");
            var capture = new RecordingIndicatorBounds(vs.X + 200, vs.Y + 200, 400, 300);
            var plan = RecordingIndicatorGeometry.ComputePresentationPlan(
                rec, capture, parentRec, DefaultLabelSize, vs);

            using var form = new RecordingIndicatorForm(
                "r1", plan, DateTime.UtcNow, 30, "inner", FakeAffinity(true));

            var handle = form.Handle;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.Equal(AutoScaleMode.None, form.AutoScaleMode);

            var expectedClientBounds = new Rectangle(
                plan.LabelBounds.X - form.BoundsForTests.X,
                plan.LabelBounds.Y - form.BoundsForTests.Y,
                plan.LabelBounds.Width,
                plan.LabelBounds.Height);
            Assert.Equal(expectedClientBounds, form.LabelBoundsForTests);
        });
    }

    [Fact]
    public void VerifyAndBuildForms_DpiMismatch_RetriesWithActualDpiBeforeShow()
    {
        RunOnSta(() =>
        {
            const int plannedDpi = 96;
            const int actualDpi = 192;
            var audit = new CaptureAuditLogger();
            var resolverDpi = new DisplayDpiInfo("test_display", new Rectangle(0, 0, 1920, 1080), plannedDpi, plannedDpi, plannedDpi / 96f, false, null);

            RecordingIndicatorForm CreateIndicator(string id, RecordingIndicatorPresentation presentation, DateTime started, int? duration, string? role)
            {
                var form = new RecordingIndicatorForm(id, presentation, started, duration, role, FakeAffinity(true));
                _ = form.Handle; // trigger OnHandleCreated so our reflection set is not overwritten later
                SetActualWindowDpi(form, actualDpi);
                return form;
            }

            RecordingStopControlForm CreateStopControl(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo dpi, CaptureVisibilityMode mode)
            {
                var form = new RecordingStopControlForm(id, bounds, size, dpi, mode, textProvider: null, displayAffinity: FakeAffinity(true));
                _ = form.Handle;
                SetActualWindowDpi(form, actualDpi);
                return form;
            }

            var mgr = new RecordingIndicatorManager(audit, _ => { }, CreateIndicator, CreateStopControl, new ForcedDpiResolver(resolverDpi));

            var parent = MakeRecording((0, 0, 1200, 900), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((300, 300, 500, 400), "inner", parent.RecordingId, "session-a");

            var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen);
            Assert.NotNull(plan);
            Assert.Equal(plannedDpi, plan!.DpiInfo.DpiX);

            var verified = mgr.VerifyAndBuildForms(inner, plan, parent);
            try
            {
                Assert.NotNull(verified);
                Assert.True(verified!.Retried);
                Assert.Equal(actualDpi, verified.FinalPlan.DpiInfo.DpiX);
            }
            finally
            {
                verified?.Indicator.Dispose();
                verified?.StopControl.Dispose();
            }
        });
    }

    [Fact]
    public void VerifyAndBuildForms_DpiMatch_DoesNotRetry()
    {
        RunOnSta(() =>
        {
            const int plannedDpi = 96;
            var audit = new CaptureAuditLogger();
            var resolverDpi = new DisplayDpiInfo("test_display", new Rectangle(0, 0, 1920, 1080), plannedDpi, plannedDpi, plannedDpi / 96f, false, null);

            RecordingIndicatorForm CreateIndicator(string id, RecordingIndicatorPresentation presentation, DateTime started, int? duration, string? role)
            {
                var form = new RecordingIndicatorForm(id, presentation, started, duration, role, FakeAffinity(true));
                _ = form.Handle;
                SetActualWindowDpi(form, plannedDpi);
                return form;
            }

            RecordingStopControlForm CreateStopControl(string id, RecordingStopControlBounds bounds, Size size, DisplayDpiInfo dpi, CaptureVisibilityMode mode)
            {
                var form = new RecordingStopControlForm(id, bounds, size, dpi, mode, textProvider: null, displayAffinity: FakeAffinity(true));
                _ = form.Handle;
                SetActualWindowDpi(form, plannedDpi);
                return form;
            }

            var mgr = new RecordingIndicatorManager(audit, _ => { }, CreateIndicator, CreateStopControl, new ForcedDpiResolver(resolverDpi));

            var parent = MakeRecording((0, 0, 1200, 900), "outer", nestedSessionId: "session-a");
            var inner = MakeRecording((300, 300, 500, 400), "inner", parent.RecordingId, "session-a");

            var plan = mgr.ComputeControlPlan(inner, parent, SystemInformation.VirtualScreen);
            Assert.NotNull(plan);
            Assert.Equal(plannedDpi, plan!.DpiInfo.DpiX);

            var verified = mgr.VerifyAndBuildForms(inner, plan, parent);
            try
            {
                Assert.NotNull(verified);
                Assert.False(verified!.Retried);
                Assert.Equal(plannedDpi, verified.FinalPlan.DpiInfo.DpiX);
            }
            finally
            {
                verified?.Indicator.Dispose();
                verified?.StopControl.Dispose();
            }
        });
    }
}
