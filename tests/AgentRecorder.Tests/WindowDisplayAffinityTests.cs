using System;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the display-affinity adapter seam.
/// </summary>
public class WindowDisplayAffinityTests
{
    [Fact]
    public void SetExcludeFromCapture_HookReturnsTrue_Succeeds()
    {
        var adapter = new WindowDisplayAffinity((hWnd, mode) => true, hWnd => (true, Native.WDA_EXCLUDEFROMCAPTURE));

        Assert.True(adapter.SetExcludeFromCapture((IntPtr)1));
    }

    [Fact]
    public void SetExcludeFromCapture_HookReturnsFalse_ReturnsFalse()
    {
        var adapter = new WindowDisplayAffinity((hWnd, mode) => false, null);

        Assert.False(adapter.SetExcludeFromCapture((IntPtr)1));
    }

    [Fact]
    public void SetExcludeFromCapture_HookThrows_PropagatesException()
    {
        var adapter = new WindowDisplayAffinity((hWnd, mode) => throw new InvalidOperationException("test boom"), null);

        var ex = Assert.Throws<InvalidOperationException>(() => adapter.SetExcludeFromCapture((IntPtr)1));
        Assert.Equal("test boom", ex.Message);
    }

    [Fact]
    public void GetAffinity_HookReturnsValue_ReturnsAffinity()
    {
        var adapter = new WindowDisplayAffinity(null, hWnd => (true, Native.WDA_EXCLUDEFROMCAPTURE));

        bool ok = adapter.GetAffinity((IntPtr)1, out uint affinity);
        Assert.True(ok);
        Assert.Equal(Native.WDA_EXCLUDEFROMCAPTURE, affinity);
    }

    [Fact]
    public void GetAffinity_HookReturnsFalse_ReturnsFalse()
    {
        var adapter = new WindowDisplayAffinity(null, hWnd => (false, Native.WDA_NONE));

        bool ok = adapter.GetAffinity((IntPtr)1, out uint affinity);
        Assert.False(ok);
        Assert.Equal(Native.WDA_NONE, affinity);
    }
}
