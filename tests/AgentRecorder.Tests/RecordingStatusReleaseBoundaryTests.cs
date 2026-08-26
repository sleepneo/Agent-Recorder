using System;
using System.IO;
using System.Text;
using AgentRecorder.App;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RecordingStatusReleaseBoundaryTests
{
    [Fact]
    public void ReleaseBuild_ExcludesDebugRecordingStatusPreviewHostAndArgumentEntryPoint()
    {
        if (!AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            return;

        var appAssembly = typeof(RecordingIndicatorForm).Assembly;
        Assert.Null(appAssembly.GetType("AgentRecorder.App.RecordingStatusStylePreviewHost", throwOnError: false));

        var appAssemblyPath = appAssembly.Location;
        Assert.True(File.Exists(appAssemblyPath));
        var image = File.ReadAllBytes(appAssemblyPath);
        var argumentBytes = Encoding.UTF8.GetBytes("--recording-status-style-preview");
        Assert.True(image.AsSpan().IndexOf(argumentBytes) < 0,
            "Release app image still contains the Debug recording-status preview argument literal.");
    }
}
