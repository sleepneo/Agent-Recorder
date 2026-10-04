using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class FutureWindowProcessIdentityTests
{
    private static readonly FutureWindowExecutableIdentity Identity = new(
        1, @"C:\Player\player.exe", "00AB12CD:0000000000000042",
        new string('a', 64), "CN=Publisher", new string('b', 64));

    [Fact]
    public void EligibleWindowSetRequiresOneExactRequestedWindow()
    {
        var first = Snapshot("window_10", 10, 100);
        var second = Snapshot("window_11", 11, 100);

        Assert.Equal("eligible_window_not_found",
            FutureWindowProcessIdentity.ValidateEligibleSnapshots(Array.Empty<FutureWindowProcessSnapshot>(), "window_10", out _));
        Assert.Equal("multiple_eligible_windows",
            FutureWindowProcessIdentity.ValidateEligibleSnapshots(new[] { first, second }, "window_10", out _));
        Assert.Equal("window_id_not_unique_eligible_target",
            FutureWindowProcessIdentity.ValidateEligibleSnapshots(new[] { first }, "window_11", out _));
        Assert.Null(FutureWindowProcessIdentity.ValidateEligibleSnapshots(new[] { first }, "window_10", out var selected));
        Assert.Same(first, selected);
    }

    [Fact]
    public void CandidateShapeCountsOnlyOneIndependentUserFacingSurface()
    {
        var main = Candidate((nint)10);
        var auxiliaries = new[]
        {
            main with { WindowHandle = (nint)11, IsVisible = false },
            main with { WindowHandle = (nint)12, IsIconic = true },
            main with { WindowHandle = (nint)13, IsRoot = false },
            main with { WindowHandle = (nint)14, HasOwner = true },
            main with { WindowHandle = (nint)15, IsToolWindow = true },
            main with { WindowHandle = (nint)16, DoesNotActivate = true },
            main with { WindowHandle = (nint)17, HasTitle = false },
            main with { WindowHandle = (nint)18, Width = 0 },
            main with { WindowHandle = (nint)19, IntersectsActiveDisplay = false },
        };

        Assert.True(main.IsEligible);
        Assert.Equal(new[]
        {
            "window_not_visible", "window_minimized", "window_not_root",
            "window_owned_auxiliary", "window_tool_window", "window_nonactivating_transient",
            "window_title_missing", "window_zero_area", "window_off_surface"
        }, auxiliaries.Select(candidate => candidate.IneligibilityReasonCode));
        Assert.Single(new[] { main }.Concat(auxiliaries), candidate => candidate.IsEligible);
    }

    [Fact]
    public void CandidateSetChangeRejectsNewRealContentWindowButIgnoresNewAuxiliaryHwnd()
    {
        var main = Snapshot("window_10", 10, 100);
        var preflight = new[] { Candidate((nint)10) }.Where(candidate => candidate.IsEligible)
            .Select(ToSnapshot).ToArray();
        Assert.Null(FutureWindowProcessIdentity.ValidateEligibleSnapshots(preflight, "window_10", out var selected));
        Assert.Equal(main, selected);

        var withAuxiliary = new[]
        {
            Candidate((nint)10), Candidate((nint)11) with { HasOwner = true }
        }.Where(candidate => candidate.IsEligible).Select(ToSnapshot).ToArray();
        Assert.Null(FutureWindowProcessIdentity.ValidateEligibleSnapshots(withAuxiliary, "window_10", out _));

        var withSecondContentWindow = new[]
        {
            Candidate((nint)10), Candidate((nint)11)
        }.Where(candidate => candidate.IsEligible).Select(ToSnapshot).ToArray();
        Assert.Equal("multiple_eligible_windows",
            FutureWindowProcessIdentity.ValidateEligibleSnapshots(withSecondContentWindow, "window_10", out selected));
        Assert.Null(selected);
    }

    [Theory]
    [InlineData("multiple_eligible_windows")]
    [InlineData("eligible_window_not_found")]
    public void RejectedCandidateValidationCannotReachProofOrCaptureBackend(string reasonCode)
    {
        var validation = FutureWindowIdentityValidation.Rejected(reasonCode);
        var proofIssueCalls = 0;
        var backendStartCalls = 0;

        if (FutureWindowProcessIdentity.TryGetValidatedHold(validation, out _, out _))
        {
            proofIssueCalls++;
            backendStartCalls++;
        }

        Assert.Equal(0, proofIssueCalls);
        Assert.Equal(0, backendStartCalls);
        Assert.Equal(reasonCode, Assert.IsType<string>(GetFailureReason(validation)));
    }

    [Fact]
    public void PublicWindowDtoExposesStructuralEligibilityReason()
    {
        var window = new SystemQuery.WindowInfo(
            "window_11", "Player helper", "PotPlayerMini64.exe", 100, false, false,
            new SystemQuery.Bounds(10, 20, 32, 32))
        {
            capture_eligibility_reason_code = "window_tool_window"
        };

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(window));
        Assert.Equal("window_tool_window",
            json.RootElement.GetProperty("capture_eligibility_reason_code").GetString());
    }

    [Fact]
    public void StartBoundaryRejectsAnyChangedWindowProcessOrExecutableEvidence()
    {
        var approved = Snapshot("window_10", 10, 100);

        Assert.True(FutureWindowProcessIdentity.IsSamePinnedTarget(approved, Snapshot("window_10", 10, 100), Identity));
        Assert.False(FutureWindowProcessIdentity.IsSamePinnedTarget(approved, Snapshot("window_11", 11, 100), Identity));
        Assert.False(FutureWindowProcessIdentity.IsSamePinnedTarget(approved,
            Snapshot("window_10", 10, 101), Identity));
        Assert.False(FutureWindowProcessIdentity.IsSamePinnedTarget(approved,
            Snapshot("window_10", 10, 100) with { ExecutableIdentity = Identity with { Sha256 = new string('c', 64) } }, Identity));
        Assert.False(FutureWindowProcessIdentity.IsSamePinnedTarget(approved,
            Snapshot("window_10", 10, 100) with { ProcessUserSid = "S-1-5-21-other" }, Identity));
    }

    private static FutureWindowProcessSnapshot Snapshot(string id, long hwnd, int processId) => new(
        id, new nint(hwnd), processId, 123456789, "S-1-5-21-test", 1,
        Identity.CanonicalPath, Identity);

    private static FutureWindowWindowCandidate Candidate(nint hwnd) => new(
        hwnd, 100, true, false, true, false, false, false, true,
        10, 20, 1280, 720, true);

    private static FutureWindowProcessSnapshot ToSnapshot(FutureWindowWindowCandidate candidate) =>
        Snapshot($"window_{candidate.WindowHandle.ToInt64()}", candidate.WindowHandle.ToInt64(), candidate.ProcessId);

    private static string? GetFailureReason(FutureWindowIdentityValidation validation) =>
        FutureWindowProcessIdentity.TryGetValidatedHold(validation, out _, out var reason)
            ? null
            : reason;
}
