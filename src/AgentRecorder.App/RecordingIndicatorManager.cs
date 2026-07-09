using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AgentRecorder.Core;
using AgentRecorder.Logging;

namespace AgentRecorder.App;

/// <summary>
/// Manages the lifecycle of <see cref="RecordingIndicatorForm"/> windows for active recordings.
/// Thread-safe for UI-thread use; all public methods must be called on the WinForms UI thread.
/// </summary>
internal sealed class RecordingIndicatorManager
{
    private readonly Dictionary<string, RecordingIndicatorForm> _indicators = new();
    private readonly AuditLogger _audit;
    private readonly Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> _formFactory;

    public RecordingIndicatorManager(AuditLogger audit)
        : this(audit, DefaultFormFactory)
    {
    }

    internal RecordingIndicatorManager(
        AuditLogger audit,
        Func<string, RecordingIndicatorBounds, DateTime, int?, string?, RecordingIndicatorForm> formFactory)
    {
        _audit = audit;
        _formFactory = formFactory;
    }

    private static RecordingIndicatorForm DefaultFormFactory(
        string recordingId,
        RecordingIndicatorBounds bounds,
        DateTime startedAtUtc,
        int? durationSeconds,
        string? nestedRole)
    {
        return new RecordingIndicatorForm(recordingId, bounds, startedAtUtc, durationSeconds, nestedRole);
    }

    /// <summary>
    /// Returns a snapshot of current indicator forms for tests.
    /// </summary>
    internal IReadOnlyDictionary<string, RecordingIndicatorForm> IndicatorsForTests => new Dictionary<string, RecordingIndicatorForm>(_indicators);

    /// <summary>
    /// Shows or replaces the indicator for the given recording.
    /// </summary>
    public void ShowFor(Recording recording)
    {
        if (recording == null) throw new ArgumentNullException(nameof(recording));

        CloseFor(recording.Id, "recording_indicator.replaced");

        var bounds = recording.Config.Bounds;
        var indicatorBounds = new RecordingIndicatorBounds(bounds.x, bounds.y, bounds.w, bounds.h);
        var clamped = RecordingIndicatorGeometry.TryClampToVirtualScreen(indicatorBounds);

        if (clamped == null)
        {
            var reason = bounds.w <= 0 || bounds.h <= 0 ? "invalid_bounds" : "outside_virtual_screen";
            _audit.Log("recording_indicator.skipped", new
            {
                recording_id = recording.Id,
                reason,
                source_type = recording.SourceType,
                bounds = new { x = bounds.x, y = bounds.y, w = bounds.w, h = bounds.h }
            });
            return;
        }

        var form = _formFactory(
            recording.Id,
            clamped,
            recording.StartedAtUtc,
            recording.DurationSeconds,
            recording.NestedRole);

        _indicators[recording.Id] = form;

        _audit.Log("recording_indicator.shown", new
        {
            recording_id = recording.Id,
            source_type = recording.SourceType,
            bounds = new { x = clamped.X, y = clamped.Y, w = clamped.Width, h = clamped.Height },
            duration_seconds = recording.DurationSeconds,
            nested_role = recording.NestedRole
        });

        try
        {
            form.Show();
        }
        catch (Exception ex)
        {
            _indicators.Remove(recording.Id);
            try { form.Dispose(); } catch { }
            _audit.Log("recording_indicator.show_error", new
            {
                recording_id = recording.Id,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Closes the indicator for the given recording id.
    /// </summary>
    public void CloseFor(string recordingId, string reasonAuditEvent)
    {
        if (_indicators.TryGetValue(recordingId, out var form))
        {
            _indicators.Remove(recordingId);
            try
            {
                form.CloseWithoutResult();
            }
            catch { }

            _audit.Log("recording_indicator.closed", new
            {
                recording_id = recordingId,
                reason = reasonAuditEvent
            });
        }
    }

    /// <summary>
    /// Closes all indicators.
    /// </summary>
    public void CloseAll(string reasonAuditEvent)
    {
        var ids = new List<string>(_indicators.Keys);
        foreach (var id in ids)
        {
            CloseFor(id, reasonAuditEvent);
        }
    }
}
