using System.Drawing;
using AgentRecorder.UI.Geometry;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Compatibility adapter for the WinForms selection form. It only maps
/// SystemQuery DTOs to the shared geometry DTOs; all formulas are shared.
/// </summary>
internal static class RegionSelectionGeometry
{
    public static Rectangle ToVirtualBounds(Rectangle formBounds, Rectangle clientSelectionBounds)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ToVirtualBounds(formBounds, clientSelectionBounds);

    public static (int width, int height) NormalizeEvenBounds(
        int width, int height, int minSize = 32, int maxWidth = int.MaxValue, int maxHeight = int.MaxValue)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.NormalizeEvenBounds(
            width, height, minSize, maxWidth, maxHeight);

    public static Rectangle? ClampInitialSelection(Rectangle formBounds, Rectangle virtualBounds, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ClampInitialSelection(formBounds, virtualBounds, minSize);

    public static Rectangle? ClampSelectionToClientRectangle(Rectangle formBounds, Rectangle clientBounds, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ClampSelectionToClientRectangle(formBounds, clientBounds, minSize);

    public static Rectangle? ClampSizedSelectionToClientRectangle(Rectangle formBounds, Rectangle clientBounds, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ClampSizedSelectionToClientRectangle(formBounds, clientBounds, minSize);

    public static Rectangle? ApplyPresetSizeAroundCenter(Rectangle formBounds, Point centerVirtual, Size targetSize, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ApplyPresetSizeAroundCenter(formBounds, centerVirtual, targetSize, minSize);

    public static Rectangle? FitAspectRatio(Rectangle formBounds, Point centerVirtual, double aspectRatio, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.FitAspectRatio(formBounds, centerVirtual, aspectRatio, minSize);

    public static Point GetVirtualScreenCenter(Rectangle formBounds)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.GetVirtualScreenCenter(formBounds);

    public static Rectangle ClampSelectionAfterDrag(
        Rectangle current, Rectangle clientBounds, SnapEdgeMask movableEdges,
        bool preserveSize = false, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ClampSelectionAfterDrag(
            current, clientBounds, movableEdges, preserveSize, minSize);

    public static Rectangle ApplySnapping(
        Rectangle current, Rectangle clientBounds, IEnumerable<Rectangle> targets, int threshold,
        SnapEdgeMask movableEdges, bool preserveSize = false, bool enabled = true, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ApplySnapping(
            current, clientBounds, targets, threshold, movableEdges, preserveSize, enabled, minSize);

    public static string? FindDisplayId(Rectangle bounds, IEnumerable<SystemQuery.DisplayInfo> displays)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.FindDisplayId(bounds, MapDisplays(displays));

    public static string? FindDisplayIdByOverlap(Rectangle bounds, IEnumerable<SystemQuery.DisplayInfo> displays)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.FindDisplayIdByOverlap(bounds, MapDisplays(displays));

    public static Rectangle? ComputeWindowClientBounds(Rectangle formBounds, SystemQuery.WindowInfo window, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ComputeWindowClientBounds(formBounds, MapWindow(window), minSize);

    public static Rectangle? ComputeWindowPickBounds(Rectangle formBounds, SystemQuery.WindowInfo window, int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.ComputeWindowPickBounds(formBounds, MapWindow(window), minSize);

    public static List<Rectangle> GenerateSnapTargets(
        Rectangle formBounds,
        IEnumerable<SystemQuery.DisplayInfo> displays,
        IEnumerable<SystemQuery.WindowInfo> windows,
        int minSize = 32)
        => AgentRecorder.UI.Geometry.RegionSelectionGeometry.GenerateSnapTargets(
            formBounds, MapDisplays(displays), windows.Select(MapWindow), minSize);

    private static IEnumerable<GeometryDisplay> MapDisplays(IEnumerable<SystemQuery.DisplayInfo> displays)
        => displays.Select(display => new GeometryDisplay(
            display.id,
            new Rectangle(display.bounds.x, display.bounds.y, display.bounds.width, display.bounds.height)));

    private static GeometryWindow MapWindow(SystemQuery.WindowInfo window)
        => new(
            window.id,
            new Rectangle(window.bounds.x, window.bounds.y, window.bounds.width, window.bounds.height),
            window.is_minimized,
            !string.IsNullOrWhiteSpace(window.title));
}
