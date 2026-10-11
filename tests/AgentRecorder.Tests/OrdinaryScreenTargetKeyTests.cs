using System.Globalization;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class OrdinaryScreenTargetKeyTests
{
    internal static CapturePlan Plan(string kind = "region", string? identity = "synthetic:stable:display", string? semantics = null,
        CapturePlanBounds? bounds = null, CapturePlanBounds? display = null,
        DisplayIdentityResolutionStatus status = DisplayIdentityResolutionStatus.Resolved, string coordinate = "virtual_screen",
        string backend = "ffmpeg-region", string title = "Synthetic", AudioCaptureSourceKind audio = AudioCaptureSourceKind.None) =>
        new("default", backend, new("default", backend, "test", "fresh_probe", 0, false), semantics ?? (kind == "display" ? "display_surface" : "region_rectangle"),
            kind, title, new nint(123), bounds ?? new(-100, 20, 640, 480), identity, display ?? new(-1920, 0, 3840, 1080), "display_1", status,
            audio, "private-audio", "private-name", true, coordinateSpace: coordinate, targetWindowProcessId: 55);

    [Fact]
    public void FixedEncodingHasGoldenVectorAndNoPrivateText()
    {
        var key = OrdinaryScreenTargetKey.Create(Plan())!;
        Assert.Equal("target:v1:region:aacff74f6dc6d9532b84651fad18a7bfd7e52827837b84ba52e3437c5b78e465", key);
        Assert.True(OrdinaryScreenTargetKey.IsValid(key)); Assert.DoesNotContain("synthetic", key); Assert.DoesNotContain("private", key);
        Assert.NotEqual(key, OrdinaryScreenTargetKey.Create(Plan(kind: "display")));
    }

    [Theory]
    [InlineData("en-US")] [InlineData("ar-SA")] [InlineData("tr-TR")] [InlineData("fr-FR")]
    public void SameFrozenPixelsIgnoreCultureBackendTitleAndAudio(string culture)
    {
        var expected = OrdinaryScreenTargetKey.Create(Plan()); var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, OrdinaryScreenTargetKey.Create(Plan(backend: "wgc-display-av-split", title: "other|:title", audio: AudioCaptureSourceKind.Microphone)));
            // Profile origin is deliberately outside CapturePlan; its underlying region is identical.
            Assert.Equal(expected, OrdinaryScreenTargetKey.Create(Plan()));
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Theory]
    [InlineData("identity")] [InlineData("display-position")] [InlineData("bounds-position")] [InlineData("bounds-size")] [InlineData("semantics")]
    public void RealTargetChangesChangeKey(string change)
    {
        var changed = change switch {
            "identity" => Plan(identity: "synthetic:other"), "display-position" => Plan(display: new(-1919, 0, 3840, 1080)),
            "bounds-position" => Plan(bounds: new(-99, 20, 640, 480)), "bounds-size" => Plan(bounds: new(-100, 20, 642, 480)),
            _ => Plan(semantics: "screen_rectangle") };
        Assert.NotEqual(OrdinaryScreenTargetKey.Create(Plan()), OrdinaryScreenTargetKey.Create(changed));
    }

    [Theory]
    [InlineData(DisplayIdentityResolutionStatus.Unresolved)] [InlineData(DisplayIdentityResolutionStatus.Ambiguous)]
    [InlineData(DisplayIdentityResolutionStatus.Missing)] [InlineData(DisplayIdentityResolutionStatus.Unavailable)] [InlineData(DisplayIdentityResolutionStatus.Conflict)]
    public void ImplicitLegacyResolutionIsInsufficient(DisplayIdentityResolutionStatus status) => Assert.Null(OrdinaryScreenTargetKey.Create(Plan(status: status)));

    [Theory]
    [InlineData("window")] [InlineData("fixed_region_profile")] [InlineData("screenshot_series")] [InlineData("unknown")]
    public void OnlyUnderlyingScreenKindsAreSupported(string kind) => Assert.Null(OrdinaryScreenTargetKey.Create(Plan(kind: kind)));

    [Fact]
    public void BoundsIdentityCoordinateAndSemanticFailuresReturnNull()
    {
        foreach (var plan in new[] { Plan(identity: null), Plan(identity: ""), Plan(identity: " bad "), Plan(identity: "a\0b"), Plan(identity: "\ud800"), Plan(identity: new string('x', 4097)),
            Plan(bounds: new(0,0,0,1)), Plan(display: new(0,0,-1,1)), Plan(bounds: new(1900,20,640,480)),
            Plan(bounds: new(int.MaxValue,0,2,2)), Plan(coordinate: "client_area"), Plan(semantics: "window_surface"),
            Plan(semantics: "display_surface"), Plan(kind: "display", semantics: "region_rectangle") }) Assert.Null(OrdinaryScreenTargetKey.Create(plan));
        Assert.Null(OrdinaryScreenTargetKey.Create(null));
        Assert.NotNull(OrdinaryScreenTargetKey.Create(Plan(identity: new string('x',4096))));
        var missing = new CapturePlan("default","ffmpeg-region",new("default","ffmpeg-region","test","fresh_probe",0,false),"region_rectangle","region",null,0,null);
        Assert.Null(OrdinaryScreenTargetKey.Create(missing));
    }

    [Fact]
    public void DelimitersAndUnicodeAreLengthBoundNotConcatenated()
    {
        var keys = new[] { "a|b:c", "a:b|c", "é:中", "é|中", "a\nb" }.Select(id => OrdinaryScreenTargetKey.Create(Plan(identity:id))).ToArray();
        Assert.All(keys, key => Assert.True(OrdinaryScreenTargetKey.IsValid(key))); Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("")] [InlineData("TARGET:v1:region:")] [InlineData("target:v2:region:")] [InlineData("target:v1:window:")]
    [InlineData("target:v1:region: ")] [InlineData("target:v1:region:A")]
    public void NonCanonicalPublicFormatsAreRejected(string prefix) => Assert.False(OrdinaryScreenTargetKey.IsValid(prefix + new string('a',64)));
}
