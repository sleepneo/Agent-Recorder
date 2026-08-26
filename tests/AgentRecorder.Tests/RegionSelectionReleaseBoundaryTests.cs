using System;
using System.IO;
using AgentRecorder.App;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class RegionSelectionReleaseBoundaryTests
{
    [Fact]
    public void ReleaseBuild_ExcludesDebugRegionSelectionPreviewHostAndArgumentEntryPoint()
    {
        // This is intentionally a Release-boundary test.  A Debug test run
        // must keep the preview for desktop acceptance, so it has no Release
        // assertion to make.
        if (!AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            return;

        var appAssembly = typeof(RegionSelectionForm).Assembly;
        Assert.Null(appAssembly.GetType("AgentRecorder.App.RegionSelectionStylePreviewHost", throwOnError: false));

        // The argument string is guarded by #if DEBUG in Program.cs.  Check
        // the built Release image as well, so a future refactor cannot leave a
        // reachable preview dispatch literal behind without failing this test.
        var appAssemblyPath = appAssembly.Location;
        Assert.True(File.Exists(appAssemblyPath));
        var image = File.ReadAllBytes(appAssemblyPath);
        var argumentBytes = System.Text.Encoding.UTF8.GetBytes("--region-selection-style-preview");
        Assert.True(image.AsSpan().IndexOf(argumentBytes) < 0,
            "Release app image still contains the Debug preview argument literal.");
    }
}
