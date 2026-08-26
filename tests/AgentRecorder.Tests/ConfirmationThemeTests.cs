using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class ConfirmationThemeTests
{
    [Fact]
    public void ThemeResolver_HighContrastTakesPrecedenceOverRegistry()
    {
        using var provider = new WindowsConfirmationThemeProvider(
            new FixedRegistryReader(ConfirmationRegistryThemeReadResult.Dark),
            new FixedHighContrastSource(true),
            new FakeThemeChangeSource());

        var resolved = provider.Resolve();

        Assert.Equal(ConfirmationThemeKind.HighContrast, resolved.Kind);
        Assert.Equal(SystemColors.Window, resolved.Palette.WindowBackground);
        Assert.Equal(SystemColors.Highlight, resolved.Palette.ApproveBackground);
    }

    [Fact]
    public void ThemeResolver_RegistryDarkAndLightAreDeterministic()
    {
        using var darkProvider = new WindowsConfirmationThemeProvider(
            new FixedRegistryReader(ConfirmationRegistryThemeReadResult.Dark),
            new FixedHighContrastSource(false),
            new FakeThemeChangeSource());
        using var lightProvider = new WindowsConfirmationThemeProvider(
            new FixedRegistryReader(ConfirmationRegistryThemeReadResult.Light),
            new FixedHighContrastSource(false),
            new FakeThemeChangeSource());

        Assert.Equal(ConfirmationThemeKind.Dark, darkProvider.Resolve().Kind);
        Assert.Equal(ConfirmationThemeKind.Light, lightProvider.Resolve().Kind);
    }

    [Fact]
    public void RegistryReader_MapsZeroNonzeroMissingInvalidAndExceptionSafely()
    {
        var cases = new (object? Value, ConfirmationRegistryThemeReadResult Expected)[]
        {
            (0, ConfirmationRegistryThemeReadResult.Dark),
            (1, ConfirmationRegistryThemeReadResult.Light),
            (-1, ConfirmationRegistryThemeReadResult.Light),
            (null, ConfirmationRegistryThemeReadResult.FallbackLight),
            ("0", ConfirmationRegistryThemeReadResult.FallbackLight)
        };

        foreach (var testCase in cases)
        {
            var reader = new WindowsConfirmationThemeRegistryReader(
                new FixedRegistryValueSource(testCase.Value));
            Assert.Equal(testCase.Expected, reader.ReadAppsUseLightTheme());
        }

        var throwingReader = new WindowsConfirmationThemeRegistryReader(
            new ThrowingRegistryValueSource());
        Assert.Equal(
            ConfirmationRegistryThemeReadResult.FallbackLight,
            throwingReader.ReadAppsUseLightTheme());
    }

    [Fact]
    public void Palette_CustomLightAndDarkTextAndCommandStatesMeetContrastTargets()
    {
        foreach (var palette in new[] { ConfirmationThemePalette.Light, ConfirmationThemePalette.Dark })
        {
            Assert.True(ConfirmationThemeContrast.Ratio(palette.PrimaryText, palette.Surface) >= 4.5);
            Assert.True(ConfirmationThemeContrast.Ratio(palette.SecondaryText, palette.Surface) >= 4.5);
            Assert.True(ConfirmationThemeContrast.Ratio(palette.ApproveText, palette.ApproveBackground) >= 4.5);
            Assert.True(ConfirmationThemeContrast.Ratio(palette.RejectText, palette.RejectBackground) >= 4.5);
            Assert.True(ConfirmationThemeContrast.Ratio(palette.FocusBorder, palette.WindowBackground) >= 3.0);
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void ConfirmationForm_AppliesThemeToKeyControlsAndPreservesDwmSurface(string kindName)
    {
        RunOnSta(() =>
        {
            var kind = Enum.Parse<ConfirmationThemeKind>(kindName);
            var provider = new FakeThemeProvider(new ConfirmationThemeSnapshot(kind, ConfirmationThemePalette.For(kind)));
            using var form = new ConfirmationForm(
                CreateItem(windowSurface: true),
                1,
                1,
                themeProvider: provider);

            var palette = ConfirmationThemePalette.For(kind);
            Assert.Equal(kind, form.ThemeKindForTests);
            Assert.Equal(palette.Surface, form.InfoPanelBackColorForTests);
            Assert.Equal(palette.SecondarySurface, form.OutputPanelBackColorForTests);
            Assert.Equal(palette.WarningText, form.WarningLabelForeColorForTests);
            Assert.Equal(palette.PreviewFallbackText, form.PreviewFallbackForeColorForTests);
            Assert.Equal(palette.ApproveBackground, form.ApproveButtonForTests!.BackColor);
            Assert.Equal(palette.RejectBackground, form.RejectButtonForTests!.BackColor);
            Assert.True(form.WindowSurfacePreviewForTests);
            Assert.True(form.WindowSurfacePreviewSurfaceIsTransparentForTests);
        });
    }

    [Fact]
    public void ThemeChange_ReappliesColorsWithoutChangingCountdownOutputRememberOrCallback()
    {
        RunOnSta(() =>
        {
            var provider = new FakeThemeProvider(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Light, ConfirmationThemePalette.Light));
            int callbackCount = 0;
            var picker = new FixedDirectoryPicker("D:\\chosen-output");
            using var form = new ConfirmationForm(
                CreateItem(d => callbackCount++, windowSurface: false),
                1,
                1,
                directoryPicker: picker,
                themeProvider: provider);

            form.RememberOutputCheckedForTests = true;
            typeof(ConfirmationForm)
                .GetMethod("ChangeOutputDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, null);

            var progress = form.CountdownRingRatioForTests;
            var timeout = form.TimeoutTextForTests;
            var output = form.OutputPathTextForTests;
            var remember = form.RememberOutputCheckedForTests;
            var appliesBefore = form.ThemeApplyCountForTests;

            provider.SetSnapshot(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Dark, ConfirmationThemePalette.Dark));

            Assert.Equal(ConfirmationThemeKind.Dark, form.ThemeKindForTests);
            Assert.True(form.ThemeApplyCountForTests > appliesBefore);
            Assert.Equal(progress, form.CountdownRingRatioForTests);
            Assert.Equal(timeout, form.TimeoutTextForTests);
            Assert.Equal(output, form.OutputPathTextForTests);
            Assert.Equal(remember, form.RememberOutputCheckedForTests);
            Assert.Equal(0, callbackCount);
        });
    }

    [Fact]
    public void Dispose_UnsubscribesThemeSourceAndLaterEventsCannotReachForm()
    {
        RunOnSta(() =>
        {
            var provider = new FakeThemeProvider(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Light, ConfirmationThemePalette.Light));
            var form = new ConfirmationForm(CreateItem(), 1, 1, themeProvider: provider);
            Assert.Equal(1, provider.SubscriberCount);

            form.Dispose();
            var applyCount = form.ThemeApplyCountForTests;
            provider.SetSnapshot(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Dark, ConfirmationThemePalette.Dark));

            Assert.True(provider.Disposed);
            Assert.Equal(0, provider.SubscriberCount);
            Assert.Equal(applyCount, form.ThemeApplyCountForTests);
        });
    }

    [Fact]
    public void ThemeSourceException_UsesLightFallbackAndPreviewModeCannotApprove()
    {
        RunOnSta(() =>
        {
            var provider = new FakeThemeProvider(
                new ConfirmationThemeSnapshot(ConfirmationThemeKind.Dark, ConfirmationThemePalette.Dark))
            {
                ThrowOnResolve = true
            };
            var item = CreateItem();
            using var form = new ConfirmationForm(
                item,
                1,
                1,
                themeProvider: provider,
                previewOnly: true);

            Assert.Equal(ConfirmationThemeKind.Light, form.ThemeKindForTests);
            Assert.True(form.PreviewOnlyForTests);
            Assert.False(form.ApproveButtonEnabledForTests);
            Assert.Same(form.RejectButtonForTests, form.DefaultActionForTests);

            typeof(ConfirmationForm)
                .GetMethod("Approve", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, null);
            Assert.False(item.CallbackCalled);
        });
    }

    [Theory]
    [InlineData("Light", 0)]
    [InlineData("Dark", 1)]
    [InlineData("HighContrast", 0)]
    public void NativeChrome_MapsThemeToBestEffortDarkModeValue(string kindName, int expected)
    {
        var kind = Enum.Parse<ConfirmationThemeKind>(kindName);
        Assert.Equal(expected, WindowsConfirmationNativeChromeAdapter.RequestedDarkModeValue(kind));
    }

    [Theory]
    [InlineData("Light", "Explorer")]
    [InlineData("Dark", "DarkMode_Explorer")]
    [InlineData("HighContrast", null)]
    public void ScrollTheme_UsesLocalThemeDataWithoutGlobalThemeSideEffects(string kindName, string? expected)
    {
        var kind = Enum.Parse<ConfirmationThemeKind>(kindName);
        Assert.Equal(expected, WindowsConfirmationScrollThemeAdapter.ThemeDataName(kind));
    }

    [Fact]
    public void NativeTheme_HandlesAreAppliedOnCreateAndThemeChangeWithoutRebuildingForm()
    {
        RunOnSta(() =>
        {
            var provider = new FakeThemeProvider(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Light, ConfirmationThemePalette.Light));
            var native = new FakeNativeChromeAdapter();
            var scroll = new FakeScrollThemeAdapter();
            using var form = new ConfirmationForm(
                CreateItem(),
                1,
                1,
                themeProvider: provider,
                nativeChromeAdapter: native,
                scrollThemeAdapter: scroll)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();
            Assert.Contains(native.AppliedThemes, item => item == ConfirmationThemeKind.Light);
            Assert.Contains(scroll.AppliedThemes, item => item == ConfirmationThemeKind.Light);

            var initialFormHandle = form.Handle;
            var initialCountdown = form.CountdownRingRatioForTests;
            provider.SetSnapshot(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Dark, ConfirmationThemePalette.Dark));
            Application.DoEvents();
            provider.SetSnapshot(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.HighContrast, ConfirmationThemePalette.HighContrast));
            Application.DoEvents();
            provider.SetSnapshot(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Light, ConfirmationThemePalette.Light));
            Application.DoEvents();

            Assert.Equal(initialFormHandle, form.Handle);
            Assert.InRange(form.CountdownRingRatioForTests, 0d, initialCountdown);
            Assert.True(form.CountdownRingRatioForTests > 0d);
            Assert.Contains(native.AppliedThemes, item => item == ConfirmationThemeKind.Dark);
            Assert.Contains(native.AppliedThemes, item => item == ConfirmationThemeKind.HighContrast);
            Assert.Contains(native.AppliedThemes, item => item == ConfirmationThemeKind.Light);
            Assert.Contains(scroll.AppliedThemes, item => item == ConfirmationThemeKind.Dark);
            Assert.Contains(scroll.AppliedThemes, item => item == ConfirmationThemeKind.HighContrast);
        });
    }

    [Fact]
    public void NativeThemeFailure_IsSwallowedAndRetainsRejectDefaultWithoutApproval()
    {
        RunOnSta(() =>
        {
            int callbackCount = 0;
            var native = new FakeNativeChromeAdapter { ThrowOnApply = true };
            var scroll = new FakeScrollThemeAdapter { ThrowOnApply = true };
            var item = CreateItem(_ => callbackCount++);
            using var form = new ConfirmationForm(
                item,
                1,
                1,
                nativeChromeAdapter: native,
                scrollThemeAdapter: scroll)
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();
            Assert.Same(form.RejectButtonForTests, form.DefaultActionForTests);
            Assert.Equal(0, callbackCount);

            form.CloseWithoutResult("native_theme_failure_test");
            Application.DoEvents();
            Assert.Equal(0, callbackCount);
        });
    }

    [Fact]
    public void Dispose_StopsNativeChromeAndScrollThemeUpdates()
    {
        RunOnSta(() =>
        {
            var provider = new FakeThemeProvider(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Light, ConfirmationThemePalette.Light));
            var native = new FakeNativeChromeAdapter();
            var scroll = new FakeScrollThemeAdapter();
            var form = new ConfirmationForm(
                CreateItem(),
                1,
                1,
                themeProvider: provider,
                nativeChromeAdapter: native,
                scrollThemeAdapter: scroll)
            {
                EnableDelayedForegroundVerification = false
            };
            form.Show();
            Application.DoEvents();
            int nativeCalls = native.AppliedThemes.Count;
            int scrollCalls = scroll.AppliedThemes.Count;

            form.Dispose();
            provider.SetSnapshot(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Dark, ConfirmationThemePalette.Dark));

            Assert.True(native.Disposed);
            Assert.True(scroll.Disposed);
            Assert.Equal(nativeCalls, native.AppliedThemes.Count);
            Assert.Equal(scrollCalls, scroll.AppliedThemes.Count);
        });
    }

    [Fact]
    public void DarkScrollHost_RemainsScrollableToLastInfoRow()
    {
        RunOnSta(() =>
        {
            var provider = new FakeThemeProvider(new ConfirmationThemeSnapshot(
                ConfirmationThemeKind.Dark, ConfirmationThemePalette.Dark));
            using var form = new ConfirmationForm(
                CreateItem(),
                1,
                1,
                themeProvider: provider,
                workingAreas: new[] { new Rectangle(0, 0, 800, 600) },
                fallbackWorkingArea: new Rectangle(0, 0, 800, 600))
            {
                EnableDelayedForegroundVerification = false
            };

            form.Show();
            Application.DoEvents();
            Assert.True(
                form.ContentScrollAutoScrollMinSizeForTests.Height > form.ContentScrollPanelBoundsForTests.Height,
                $"Expected content overflow; min={form.ContentScrollAutoScrollMinSizeForTests}, panel={form.ContentScrollPanelBoundsForTests}");

            var positionBeforeBottom = form.ContentScrollPositionForTests;
            form.ScrollContentToBottomForTests();
            Application.DoEvents();
            Assert.True(form.ContentScrollPositionForTests.Y <= positionBeforeBottom.Y);
            var lastRow = form.GetInfoRowBoundsForTests().Last().LabelBounds;
            Assert.True(
                form.ContentScrollPanelBoundsForTests.IntersectsWith(lastRow),
                $"Expected last info row to remain reachable; last={lastRow}, panel={form.ContentScrollPanelBoundsForTests}, position={form.ContentScrollPositionForTests}");
        });
    }

    [Fact]
    public void FormalAppAndPortableReleaseBoundaryRemainWinFormsOnly()
    {
        var root = FindRepositoryRoot();
        var appProject = File.ReadAllText(Path.Combine(root, "src", "AgentRecorder.App", "AgentRecorder.App.csproj"));
        var releaseScript = File.ReadAllText(Path.Combine(root, "scripts", "build-portable-release.ps1"));
        var appSources = string.Join("\n", Directory.GetFiles(
            Path.Combine(root, "src", "AgentRecorder.App"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        var productionSurface = appProject + releaseScript + appSources;
        Assert.DoesNotContain("<UseWPF>", productionSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PresentationFramework", productionSurface, StringComparison.OrdinalIgnoreCase);
    }

    private static PendingConfirmationItem CreateItem(
        Action<ConfirmationDecision>? callback = null,
        bool windowSurface = false)
    {
        var now = DateTime.UtcNow;
        var presentation = new RecordingConfirmationPresentation
        {
            Summary = new RecordingRequestSummary
            {
                Mode = "video",
                Source = "display: primary",
                Audio = "No audio",
                AudioSourceKind = "none",
                Duration = "30s",
                CountdownSeconds = 0,
                Output = "C:\\recordings\\capture.mp4"
            },
            RecordingId = "rec_theme",
            ConfirmationId = "conf_theme",
            TimeoutSeconds = 60,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(60),
            SourceType = "display",
            SourceTitle = "Primary display",
            SourceApplication = "Agent Recorder",
            CaptureSemantics = windowSurface ? "window_surface" : "display_surface",
            PreviewSemantics = windowSurface ? "DWM window preview" : "display preview",
            PlannedBackend = "gdi",
            OutputKind = "mp4_file"
        };
        return new PendingConfirmationItem(presentation, callback ?? (_ => { }));
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new TargetInvocationException(error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AgentRecorder.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class FixedRegistryReader : IConfirmationThemeRegistryReader
    {
        private readonly ConfirmationRegistryThemeReadResult _result;
        public FixedRegistryReader(ConfirmationRegistryThemeReadResult result) => _result = result;
        public ConfirmationRegistryThemeReadResult ReadAppsUseLightTheme() => _result;
    }

    private sealed class FixedHighContrastSource : IConfirmationHighContrastSource
    {
        public bool IsHighContrast { get; }
        public FixedHighContrastSource(bool value) => IsHighContrast = value;
    }

    private sealed class FixedRegistryValueSource : IConfirmationThemeRegistryValueSource
    {
        private readonly object? _value;
        public FixedRegistryValueSource(object? value) => _value = value;
        public object? ReadValue(string subKey, string valueName) => _value;
    }

    private sealed class ThrowingRegistryValueSource : IConfirmationThemeRegistryValueSource
    {
        public object? ReadValue(string subKey, string valueName) => throw new InvalidOperationException("fake registry failure");
    }

    private sealed class FakeThemeChangeSource : IConfirmationThemeChangeSource
    {
        public event EventHandler? ThemeChanged
        {
            add { }
            remove { }
        }

        public void Dispose() { }
    }

    private sealed class FakeThemeProvider : IConfirmationThemeProvider
    {
        private ConfirmationThemeSnapshot _snapshot;
        public bool ThrowOnResolve { get; set; }
        public bool Disposed { get; private set; }
        public event EventHandler? ThemeChanged;
        public int SubscriberCount => ThemeChanged?.GetInvocationList().Length ?? 0;

        public FakeThemeProvider(ConfirmationThemeSnapshot snapshot) => _snapshot = snapshot;

        public ConfirmationThemeSnapshot Resolve()
        {
            if (ThrowOnResolve)
                throw new InvalidOperationException("fake theme source failure");
            return _snapshot;
        }

        public void SetSnapshot(ConfirmationThemeSnapshot snapshot)
        {
            _snapshot = snapshot;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FixedDirectoryPicker : IOutputDirectoryPicker
    {
        private readonly string _path;
        public FixedDirectoryPicker(string path) => _path = path;
        public string? PickDirectory(string initialDirectory) => _path;
    }

    private sealed class FakeNativeChromeAdapter : IConfirmationNativeChromeAdapter
    {
        public List<ConfirmationThemeKind> AppliedThemes { get; } = new();
        public bool ThrowOnApply { get; set; }
        public bool Disposed { get; private set; }

        public bool Apply(IntPtr windowHandle, ConfirmationThemeKind themeKind)
        {
            if (ThrowOnApply)
                throw new InvalidOperationException("fake native chrome failure");
            AppliedThemes.Add(themeKind);
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeScrollThemeAdapter : IConfirmationScrollThemeAdapter
    {
        public List<ConfirmationThemeKind> AppliedThemes { get; } = new();
        public bool ThrowOnApply { get; set; }
        public bool Disposed { get; private set; }

        public bool Apply(Control scrollHost, ConfirmationThemeKind themeKind)
        {
            if (ThrowOnApply)
                throw new InvalidOperationException("fake scroll theme failure");
            AppliedThemes.Add(themeKind);
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
