using AgentRecorder.Windows;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

public sealed class FutureWindowCandidateDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public FutureWindowCandidateDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void OptInLivePotPlayerDiagnostic_ReportsOnlyBoundedStructuralAttributes()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("AGENTRECORDER_FUTURE_WINDOW_DIAGNOSTIC"),
                "1", StringComparison.Ordinal))
            return;

        var result = FutureWindowProcessIdentity.DiagnoseExecutableWindows(
            @"D:\app\PotPlayer\PotPlayerMini64.exe", maximumCandidates: 32);
        _output.WriteLine($"succeeded={result.EnumerationSucceeded}; failure={result.FailureCode}; win32_error={result.NativeErrorCode}; candidates={result.Candidates.Count}; scanned={result.ScannedWindowCount}; truncated={result.Truncated}; inspection_failures={result.InspectionFailureCount}");
        if (!result.EnumerationSucceeded)
        {
            _output.WriteLine("Detailed HWND attribution is unavailable in this test host; no root cause is inferred from this probe.");
            return;
        }

        foreach (var candidate in result.Candidates)
        {
            _output.WriteLine(
                $"hwnd=0x{candidate.WindowHandle.ToInt64():X}; pid={candidate.ProcessId}; " +
                $"visible={candidate.IsVisible}; iconic={candidate.IsIconic}; root={candidate.IsRoot}; " +
                $"owner={candidate.HasOwner}; tool={candidate.IsToolWindow}; no_activate={candidate.DoesNotActivate}; " +
                $"bounds=({candidate.X},{candidate.Y},{candidate.Width},{candidate.Height}); " +
                $"title_present={candidate.HasTitle}; display_intersection={candidate.IntersectsActiveDisplay}; " +
                $"eligible={candidate.IsEligible}; reason={candidate.IneligibilityReasonCode ?? "eligible_content_window"}");
        }
        Assert.InRange(result.Candidates.Count, 0, 32);
        Assert.InRange(result.ScannedWindowCount, 0, 4097);
    }
}
