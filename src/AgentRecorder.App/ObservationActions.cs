using System.Diagnostics;
using System.Windows.Forms;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal sealed record ObservationActiveRecording(RecordingUiPresentation Presentation, bool CanStop);
internal interface IObservationActiveGateway
{
    IReadOnlyList<ObservationActiveRecording> GetObservationSnapshot();
    Task<ObservationStopResult> StopObservationRecordingAsync(string recordingId);
}
internal enum ObservationStopOutcome { Requested, NotStoppable, Busy, Failed, Closed }
internal sealed record ObservationStopResult(ObservationStopOutcome Outcome, string? ErrorCode = null);

internal sealed class ObservationStopController(IObservationActiveGateway gateway) : IDisposable
{
    private readonly object _gate = new();
    private bool _busy, _closed;
    internal bool IsBusy { get { lock (_gate) return _busy; } }
    internal async Task<ObservationStopResult> StopAsync(string id)
    {
        lock (_gate)
        {
            if (_closed) return new(ObservationStopOutcome.Closed);
            if (_busy) return new(ObservationStopOutcome.Busy);
            _busy = true;
        }
        ObservationStopResult result;
        try
        {
            // Called from the UI thread; never read the tray dictionary on a worker.
            var target = gateway.GetObservationSnapshot().SingleOrDefault(r => r.Presentation.RecordingId == id);
            result = target is not { CanStop: true } ? new(ObservationStopOutcome.NotStoppable)
                : await gateway.StopObservationRecordingAsync(id).ConfigureAwait(false);
        }
        catch (Exception ex) { result = new(ObservationStopOutcome.Failed, ex is ApiException api ? api.Code : "stop_failed"); }
        lock (_gate) { _busy = false; return _closed ? new(ObservationStopOutcome.Closed) : result; }
    }
    public void Dispose() { lock (_gate) _closed = true; }
}

internal interface IObservationDesktopActions
{
    bool FileExists(string path);
    void OpenFolder(string directory);
    void Copy(string value);
}
internal sealed class ObservationDesktopActions : IObservationDesktopActions
{
    public bool FileExists(string path) => File.Exists(path);
    public void Copy(string value) => Clipboard.SetText(value);
    public void OpenFolder(string directory)
    {
        // Start Explorer with one literal parent-directory argument. Never shell
        // execute the output file, an arbitrary command, or a fallback directory.
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = false };
        start.ArgumentList.Add(directory);
        Process.Start(start)?.Dispose();
    }
}
internal static class ObservationOutputAction
{
    internal static bool HasRecordedPath(UnifiedHistoryEntry? entry)
    {
        try
        {
            return entry is { OutputPathRecorded: true, OutputPath: { } path } && !path.Contains('\0') &&
                Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(Path.GetFileName(path)) && !string.IsNullOrEmpty(Path.GetDirectoryName(path));
        }
        catch { return false; }
    }
    internal static bool OpenFolder(UnifiedHistoryEntry entry, IObservationDesktopActions desktop)
    {
        try
        {
            if (!HasRecordedPath(entry) || !desktop.FileExists(entry.OutputPath!)) return false;
            desktop.OpenFolder(Path.GetDirectoryName(entry.OutputPath!)!);
            return true;
        }
        catch { return false; }
    }
}
