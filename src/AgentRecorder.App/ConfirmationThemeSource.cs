using Microsoft.Win32;
using System.Windows.Forms;

namespace AgentRecorder.App;

internal enum ConfirmationRegistryThemeReadResult
{
    Light,
    Dark,
    FallbackLight
}

internal interface IConfirmationThemeRegistryValueSource
{
    object? ReadValue(string subKey, string valueName);
}

internal interface IConfirmationThemeRegistryReader
{
    ConfirmationRegistryThemeReadResult ReadAppsUseLightTheme();
}

internal sealed class WindowsConfirmationThemeRegistryValueSource : IConfirmationThemeRegistryValueSource
{
    public object? ReadValue(string subKey, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
}

internal sealed class WindowsConfirmationThemeRegistryReader : IConfirmationThemeRegistryReader
{
    internal const string PersonalizeSubKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    internal const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private readonly IConfirmationThemeRegistryValueSource _source;

    public WindowsConfirmationThemeRegistryReader(IConfirmationThemeRegistryValueSource? source = null)
    {
        _source = source ?? new WindowsConfirmationThemeRegistryValueSource();
    }

    public ConfirmationRegistryThemeReadResult ReadAppsUseLightTheme()
    {
        try
        {
            var value = _source.ReadValue(PersonalizeSubKey, AppsUseLightThemeValue);
            return value switch
            {
                int integer when integer == 0 => ConfirmationRegistryThemeReadResult.Dark,
                int => ConfirmationRegistryThemeReadResult.Light,
                uint unsigned when unsigned == 0 => ConfirmationRegistryThemeReadResult.Dark,
                uint => ConfirmationRegistryThemeReadResult.Light,
                long number when number == 0 => ConfirmationRegistryThemeReadResult.Dark,
                long => ConfirmationRegistryThemeReadResult.Light,
                _ => ConfirmationRegistryThemeReadResult.FallbackLight
            };
        }
        catch
        {
            return ConfirmationRegistryThemeReadResult.FallbackLight;
        }
    }
}

internal interface IConfirmationHighContrastSource
{
    bool IsHighContrast { get; }
}

internal sealed class WindowsConfirmationHighContrastSource : IConfirmationHighContrastSource
{
    public bool IsHighContrast => SystemInformation.HighContrast;
}

internal interface IConfirmationThemeChangeSource : IDisposable
{
    event EventHandler? ThemeChanged;
}

internal sealed class WindowsConfirmationThemeChangeSource : IConfirmationThemeChangeSource
{
    private bool _disposed;

    public event EventHandler? ThemeChanged;

    public WindowsConfirmationThemeChangeSource()
    {
        try { SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; }
        catch { _disposed = true; }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed)
            return;

        // The registry and HighContrast values can be changed by several
        // Windows preference categories. Filtering here would risk missing a
        // valid theme transition, so the form re-resolves safely for each one.
        try { ThemeChanged?.Invoke(this, EventArgs.Empty); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; }
        catch { }
        ThemeChanged = null;
    }
}

internal interface IConfirmationThemeProvider : IDisposable
{
    event EventHandler? ThemeChanged;
    ConfirmationThemeSnapshot Resolve();
}

internal sealed class WindowsConfirmationThemeProvider : IConfirmationThemeProvider
{
    private readonly IConfirmationThemeRegistryReader _registryReader;
    private readonly IConfirmationHighContrastSource _highContrastSource;
    private readonly IConfirmationThemeChangeSource _changeSource;
    private bool _disposed;

    public event EventHandler? ThemeChanged
    {
        add
        {
            try { _changeSource.ThemeChanged += value; }
            catch { }
        }
        remove
        {
            try { _changeSource.ThemeChanged -= value; }
            catch { }
        }
    }

    public WindowsConfirmationThemeProvider(
        IConfirmationThemeRegistryReader? registryReader = null,
        IConfirmationHighContrastSource? highContrastSource = null,
        IConfirmationThemeChangeSource? changeSource = null)
    {
        _registryReader = registryReader ?? new WindowsConfirmationThemeRegistryReader();
        _highContrastSource = highContrastSource ?? new WindowsConfirmationHighContrastSource();
        _changeSource = changeSource ?? new WindowsConfirmationThemeChangeSource();
    }

    public ConfirmationThemeSnapshot Resolve()
    {
        try
        {
            if (_highContrastSource.IsHighContrast)
                return new ConfirmationThemeSnapshot(ConfirmationThemeKind.HighContrast, ConfirmationThemePalette.HighContrast);

            var result = _registryReader.ReadAppsUseLightTheme();
            var kind = result == ConfirmationRegistryThemeReadResult.Dark
                ? ConfirmationThemeKind.Dark
                : ConfirmationThemeKind.Light;
            return new ConfirmationThemeSnapshot(kind, ConfirmationThemePalette.For(kind));
        }
        catch
        {
            return new ConfirmationThemeSnapshot(ConfirmationThemeKind.Light, ConfirmationThemePalette.Light);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try { _changeSource.Dispose(); }
        catch { }
    }
}
