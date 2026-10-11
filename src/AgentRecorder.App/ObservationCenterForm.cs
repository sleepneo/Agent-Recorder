using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal sealed class ObservationCenterForm : Form
{
    private readonly IObservationActiveGateway _active;
    private readonly ObservationHistoryController _history;
    private readonly ObservationStopController _stop;
    private readonly IObservationDesktopActions _desktop;
    private readonly Action _openSafety;
    private readonly TimeZoneInfo _zone;
    private readonly Func<DateTimeOffset> _now;
    private IUiTextProvider _text;
    private readonly System.Windows.Forms.Timer _activeTimer = new() { Interval = 1000 };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _activeTab = new(), _historyTab = new(), _healthTab = new();
    private readonly IObservationHealthGateway _health;
    private readonly Button _healthRefresh = Button("health_refresh"), _healthCopy = Button("health_copy");
    private readonly Label _healthStatus = Label("health_status"), _healthPreviewLabel = Label("health_preview_label");
    private readonly DataGridView _healthGrid = Grid("health_grid");
    private readonly TextBox _healthDetails = Details("health_details"), _healthPreview = Details("health_preview");
    private readonly CancellationTokenSource _healthLifetime = new();
    private ObservationHealthSnapshot? _healthSnapshot;
    private bool _healthEntered, _renderingHealth, _healthBusy;
    private long _healthGeneration;
    private Task _healthOperation = Task.CompletedTask;
    internal Task HealthOperationForTests => _healthOperation;
    internal Func<CancellationToken, Task>? HealthDeadlineForTests { get; set; }
    private readonly Button _safety = Button("safety"), _close = Button("close");
    private readonly Button _stopButton = Button("stop"), _copyActive = Button("copy_active");
    private readonly Button _refresh = Button("refresh"), _next = Button("next");
    private readonly Button _copyId = Button("copy_id"), _copyPath = Button("copy_path"), _copyDetails = Button("copy_details"), _folder = Button("folder");
    private readonly Label _activeStatus = Label("active_status"), _historyStatus = Label("history_status"), _timeNote = Label("time_note");
    private readonly Label _sourceLabel = Label("source_label"), _rangeLabel = Label("range_label"), _statusLabel = Label("status_label");
    private readonly ComboBox _source = new() { Name = "source", DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ComboBox _range = new() { Name = "range", DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ComboBox _status = new() { Name = "status_filter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly DataGridView _activeGrid = Grid("active_grid"), _historyGrid = Grid("history_grid");
    private readonly TextBox _activeDetails = Details("active_details"), _historyDetails = Details("history_details");
    private bool _closed, _initialLoad, _updatingLanguage, _refreshingActive;
    private long _selectionGeneration, _historySelectionGeneration;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly object _completionGate = new();
    private readonly HashSet<TaskCompletionSource> _queuedCompletions = new();
    private bool _ownerHandleAvailable, _completionClosed, _folderBusy;
    private long _handleGeneration;
    private Task _folderOperation = Task.CompletedTask, _stopOperation = Task.CompletedTask;
    // Only the posting step is replaceable; the same owner/lifetime checks still run.
    internal Action<Action>? CompletionPostForTests { get; set; }
    internal Task FolderOperationForTests => _folderOperation;
    internal Task StopOperationForTests => _stopOperation;
    private string? _stopMessage;
    private static readonly string[] Sources = { "all", "ordinary", "plan", "future_window" };

    internal ObservationCenterForm(IObservationActiveGateway active, IUnifiedHistoryGateway history, Action openSafety,
        IUiTextProvider text, IObservationDesktopActions? desktop = null,
        TimeZoneInfo? zone = null, Func<DateTimeOffset>? now = null,
        IObservationHealthGateway? health = null)
    {
        _health = health ?? UnavailableObservationHealthGateway.Instance;
        _active = active; _text = text; _openSafety = openSafety;
        _desktop = desktop ?? new ObservationDesktopActions();
        _zone = zone ?? TimeZoneInfo.Local; _now = now ?? (() => DateTimeOffset.UtcNow);
        _history = new(history, Dispatch); _stop = new(active);
        Name = "ObservationCenterForm"; AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = false; TopMost = false; MinimumSize = new(580, 420); ClientSize = new(940, 680);
        var root = Table(2); root.Padding = new Padding(12);
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(Flow(_safety, _close), 0, 0); root.Controls.Add(_tabs, 0, 1); Controls.Add(root);
        _tabs.TabPages.AddRange(new[] { _activeTab, _historyTab, _healthTab });
        _healthGrid.Columns.AddRange(Column("name", 28), Column("state", 24), Column("fact", 48));
        var healthRoot = Table(6);
        healthRoot.RowStyles.Add(new(SizeType.AutoSize)); healthRoot.RowStyles.Add(new(SizeType.AutoSize));
        healthRoot.RowStyles.Add(new(SizeType.Percent, 45)); healthRoot.RowStyles.Add(new(SizeType.Percent, 25));
        healthRoot.RowStyles.Add(new(SizeType.AutoSize)); healthRoot.RowStyles.Add(new(SizeType.Percent, 30));
        healthRoot.Controls.Add(Flow(_healthRefresh, _healthCopy), 0, 0); healthRoot.Controls.Add(_healthStatus, 0, 1);
        healthRoot.Controls.Add(_healthGrid, 0, 2); healthRoot.Controls.Add(_healthDetails, 0, 3);
        healthRoot.Controls.Add(_healthPreviewLabel, 0, 4); healthRoot.Controls.Add(_healthPreview, 0, 5); _healthTab.Controls.Add(healthRoot);
        _healthRefresh.Click += async (_, _) => await StartHealthOperation();
        _healthCopy.Click += (_, _) => Copy(_healthPreview.Text, _healthStatus);
        _healthGrid.SelectionChanged += (_, _) => { if (!_renderingHealth) RenderHealthDetails(); };
        _activeGrid.Columns.AddRange(Column("id", 35), Column("target", 22), Column("phase", 25), Column("elapsed", 18));
        _historyGrid.Columns.AddRange(Column("kind", 24), Column("created", 24), Column("status", 18), Column("target", 18), Column("media", 24));
        var activeRoot = Table(4);
        activeRoot.RowStyles.Add(new(SizeType.AutoSize)); activeRoot.RowStyles.Add(new(SizeType.Percent, 60)); activeRoot.RowStyles.Add(new(SizeType.Percent, 40)); activeRoot.RowStyles.Add(new(SizeType.AutoSize));
        activeRoot.Controls.Add(_activeStatus, 0, 0); activeRoot.Controls.Add(_activeGrid, 0, 1); activeRoot.Controls.Add(_activeDetails, 0, 2);
        activeRoot.Controls.Add(Flow(_stopButton, _copyActive), 0, 3); _activeTab.Controls.Add(activeRoot);
        var historyRoot = Table(6);
        historyRoot.RowStyles.Add(new(SizeType.AutoSize)); historyRoot.RowStyles.Add(new(SizeType.AutoSize)); historyRoot.RowStyles.Add(new(SizeType.AutoSize));
        historyRoot.RowStyles.Add(new(SizeType.Percent, 60)); historyRoot.RowStyles.Add(new(SizeType.Percent, 40)); historyRoot.RowStyles.Add(new(SizeType.AutoSize));
        historyRoot.Controls.Add(Flow(_sourceLabel, _source, _rangeLabel, _range, _statusLabel, _status, _refresh, _next), 0, 0);
        historyRoot.Controls.Add(_timeNote, 0, 1); historyRoot.Controls.Add(_historyStatus, 0, 2); historyRoot.Controls.Add(_historyGrid, 0, 3);
        historyRoot.Controls.Add(_historyDetails, 0, 4); historyRoot.Controls.Add(Flow(_copyId, _copyPath, _copyDetails, _folder), 0, 5); _historyTab.Controls.Add(historyRoot);
        _safety.Click += (_, _) => _openSafety(); _close.Click += (_, _) => Close();
        _refresh.Click += (_, _) => { _history.Reload(_history.View.Filters); RenderHistory(); };
        _next.Click += (_, _) => { _history.NextPage(); RenderHistory(); };
        _source.SelectedIndexChanged += FiltersChanged; _range.SelectedIndexChanged += FiltersChanged; _status.SelectedIndexChanged += FiltersChanged;
        _activeGrid.SelectionChanged += (_, _) => { if (_refreshingActive) return; _selectionGeneration++; _stopMessage = null; RenderActiveDetails(); };
        _historyGrid.SelectionChanged += (_, _) => { _historySelectionGeneration++; RenderHistoryDetails(); };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            _historySelectionGeneration++; _selectionGeneration++;
            if (_tabs.SelectedTab == _healthTab && !_healthEntered && !_closed)
            { _healthEntered = true; _ = StartHealthOperation(); }
        };
        _stopButton.Click += async (_, _) => await StartStopOperation();
        _copyActive.Click += (_, _) => Copy(_activeDetails.Text, _activeStatus);
        _copyId.Click += (_, _) => { if (SelectedHistory() is { } entry) Copy(entry.EntryId, _historyStatus); };
        _copyPath.Click += (_, _) => { if (SelectedHistory() is { } entry && ObservationOutputAction.HasRecordedPath(entry)) Copy(entry.OutputPath!, _historyStatus); };
        _copyDetails.Click += (_, _) => Copy(_historyDetails.Text, _historyStatus);
        _folder.Click += async (_, _) => await StartFolderOperation();
        _history.Changed += RenderHistory; _activeTimer.Tick += (_, _) => RefreshActive();
        UpdateLanguage(text); RefreshActive(); RenderHistory();
    }
    internal ObservationHistoryController HistoryForTests => _history;
    internal bool ActiveTimerEnabledForTests => _activeTimer.Enabled;
    internal void RenderHistoryForTests() => RenderHistory();
    internal void RefreshActiveForTests() => RefreshActive();
    internal void UpdateLanguage(IUiTextProvider text)
    {
        if (_closed) return;
        _text = text; _updatingLanguage = true;
        var source = Math.Max(0, _source.SelectedIndex); var range = Math.Max(0, _range.SelectedIndex); var status = source == 1 ? Math.Max(0, _status.SelectedIndex) : 0;
        _source.Items.Clear(); _source.Items.AddRange(Sources.Select(s => (object)text.Get("Observation_Source_" + s)).ToArray()); _source.SelectedIndex = source;
        _range.Items.Clear(); _range.Items.AddRange(new object[] { text.Get("Observation_TimeAll"), text.Get("Observation_TimeToday"), text.Get("Observation_TimeSeven") }); _range.SelectedIndex = range;
        _status.Items.Clear(); _status.Items.Add(text.Get("Observation_StatusAll"));
        _status.Items.AddRange(OrdinaryRecordingHistoryStatusCodes.Supported.Select(code => (object)ObservationFormatting.State(code, text)).ToArray());
        _status.SelectedIndex = status; _status.Enabled = source == 1;
        Text = text.Get("Observation_Title"); _activeTab.Text = text.Get("Observation_Active"); _historyTab.Text = text.Get("Observation_History");
        Set(_safety, "Safety"); Set(_close, "Close"); Set(_stopButton, "Stop"); Set(_copyActive, "CopyDetails"); Set(_refresh, "Refresh"); Set(_next, "Next");
        Set(_copyId, "CopyId"); Set(_copyPath, "CopyPath"); Set(_copyDetails, "CopyDetails"); Set(_folder, "OpenFolder"); Set(_sourceLabel, "Source"); Set(_rangeLabel, "Range"); Set(_statusLabel, "StatusFilter");
        string[] activeKeys = { "RecordingId", "Target", "Status", "Elapsed" }, historyKeys = { "Kind", "Created", "Status", "Target", "Media" };
        for (var i = 0; i < activeKeys.Length; i++) _activeGrid.Columns[i].HeaderText = text.Get("Observation_" + activeKeys[i]);
        for (var i = 0; i < historyKeys.Length; i++) _historyGrid.Columns[i].HeaderText = text.Get("Observation_" + historyKeys[i]);
        _updatingLanguage = false; _selectionGeneration++; _historySelectionGeneration++; _stopMessage = null;
        _history.LanguageChanged(); RefreshActive(); RenderHistory(); RenderHealth();
    }
    private Task StartHealthOperation()
    {
        if (_closed || !_healthOperation.IsCompleted) return _healthOperation;
        _healthEntered = true;
        return _healthOperation = RefreshHealthAsync();
    }
    private async Task RefreshHealthAsync()
    {
        var generation = ++_healthGeneration; var handleGeneration = _handleGeneration;
        _healthBusy = true; RenderHealth();
        ObservationHealthOwner owner; var ownerFailed = false;
        try { owner = _health.CaptureOwner(); }
        catch { ownerFailed = true; owner = UnavailableObservationHealthGateway.Instance.CaptureOwner(); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_healthLifetime.Token);
        ObservationHealthSnapshot snapshot;
        try
        {
            if (ownerFailed) snapshot = ObservationHealthSnapshot.Partial(owner, HealthProbeState.Failed);
            else
            {
                var sample = _health.SampleAsync(owner, cancellation.Token);
                var deadline = HealthDeadlineForTests?.Invoke(cancellation.Token) ?? Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
                var completed = await Task.WhenAny(sample, deadline).ConfigureAwait(false);
                if (completed == sample) snapshot = await sample.ConfigureAwait(false);
                else
                {
                    cancellation.Cancel();
                    snapshot = ObservationHealthSnapshot.Partial(owner, _healthLifetime.IsCancellationRequested ? HealthProbeState.Cancelled : HealthProbeState.Timeout);
                    _ = ObserveLateHealthAsync(sample);
                }
            }
        }
        catch (OperationCanceledException) { snapshot = ObservationHealthSnapshot.Partial(owner, HealthProbeState.Cancelled); }
        catch { snapshot = ObservationHealthSnapshot.Partial(owner, HealthProbeState.Failed); }
        cancellation.Cancel();
        await CompleteOnOwnerAsync(handleGeneration, () =>
        {
            if (generation != _healthGeneration) return;
            _healthBusy = false; _healthSnapshot = snapshot; RenderHealth();
        }).ConfigureAwait(false);
    }
    private static async Task ObserveLateHealthAsync(Task<ObservationHealthSnapshot> sample)
    { try { await sample.ConfigureAwait(false); } catch { /* Discard stale facts and raw exceptions. */ } }
    private void RenderHealth()
    {
        if (_closed) return;
        string T(string key) => ObservationHealthFormatting.Text(_text, key);
        _healthTab.Text = T("Title"); _healthRefresh.Text = T("Refresh"); _healthCopy.Text = T("Copy");
        _healthRefresh.AccessibleName = _healthRefresh.Text; _healthCopy.AccessibleName = _healthCopy.Text;
        _healthPreviewLabel.Text = T("Preview");
        _healthGrid.Columns[0].HeaderText = T("Name"); _healthGrid.Columns[1].HeaderText = T("Status"); _healthGrid.Columns[2].HeaderText = T("Fact");
        _healthRefresh.Enabled = !_healthBusy; _healthCopy.Enabled = _healthSnapshot is not null;
        _healthStatus.Text = T(_healthBusy ? "Loading" : (_healthSnapshot?.ProbeState ?? HealthProbeState.NotChecked).ToString());
        if (_healthSnapshot is null) return;
        var selected = (_healthGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as ObservationHealthRow)?.Id;
        var rows = ObservationHealthFormatting.Rows(_healthSnapshot, _text);
        _renderingHealth = true; _healthGrid.Rows.Clear();
        foreach (var row in rows)
        {
            var index = _healthGrid.Rows.Add(row.Name, ObservationHealthFormatting.State(_text, row.State), row.Details.Replace("\r\n", " · "));
            _healthGrid.Rows[index].Tag = row;
        }
        RestoreSelection(_healthGrid, row => (row.Tag as ObservationHealthRow)?.Id == selected);
        _renderingHealth = false; RenderHealthDetails();
        SetDetails(_healthPreview, ObservationHealthFormatting.Summary(_healthSnapshot, _text, rows));
    }
    private void RenderHealthDetails() => SetDetails(_healthDetails,
        (_healthGrid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault()?.Tag as ObservationHealthRow)?.Details ?? "");
    private void Set(Control control, string key) { control.Text = _text.Get("Observation_" + key); control.AccessibleName = control.Text; }
    private void Dispatch(Action action)
    {
        // History notifications keep their existing BeginInvoke delivery mode.
        lock (_completionGate) { if (_completionClosed || !_ownerHandleAvailable) return; }
        try { BeginInvoke((Action)(() => { if (!_closed && !IsDisposed && !Disposing) action(); })); }
        catch (InvalidOperationException) when (OwnerDeliveryUnavailable()) { /* Handle lost during shutdown. */ }
    }
    private bool OwnerDeliveryUnavailable(long? generation = null)
    {
        lock (_completionGate) return _completionClosed || !_ownerHandleAvailable || (generation.HasValue && generation.Value != _handleGeneration);
    }
    private Task CompleteOnOwnerAsync(long generation, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var onOwner = Environment.CurrentManagedThreadId == _ownerThreadId;
        lock (_completionGate)
        {
            // InvokeRequired=false without a handle is not proof of ownership.
            if (_completionClosed || generation != _handleGeneration || (!onOwner && !_ownerHandleAvailable)) return Task.CompletedTask;
            _queuedCompletions.Add(completion);
        }
        void Run()
        {
            try
            {
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                    throw new InvalidOperationException("Observation completion must run on its window owner.");
                bool current;
                lock (_completionGate) current = !_completionClosed && generation == _handleGeneration;
                // All control/selection reads, including these checks, occur on the owner.
                if (current && !_closed && !IsDisposed && !Disposing) action();
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { lock (_completionGate) _queuedCompletions.Remove(completion); }
        }
        try
        {
            if (onOwner) Run();
            else if (CompletionPostForTests is { } post) post(Run);
            else BeginInvoke((Action)Run);
        }
        catch (InvalidOperationException) when (OwnerDeliveryUnavailable(generation))
        {
            // BeginInvoke can lose the handle between checking and posting.
            lock (_completionGate) _queuedCompletions.Remove(completion);
            completion.TrySetResult();
        }
        return completion.Task;
    }
    private void InvalidateOwnerDelivery(bool closing)
    {
        lock (_completionGate)
        {
            _completionClosed |= closing; _ownerHandleAvailable = false; _handleGeneration++;
            // A destroyed handle may never drain BeginInvoke; complete discarded work.
            foreach (var completion in _queuedCompletions) completion.TrySetResult();
            _queuedCompletions.Clear();
        }
        _folderBusy = false;
        _healthGeneration++; _healthBusy = false;
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); ConstrainToWorkingArea(); _activeTimer.Start();
        if (!_initialLoad) { _initialLoad = true; _history.Reload(CurrentFilters()); RenderHistory(); }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        lock (_completionGate) { _ownerHandleAvailable = !_completionClosed; _handleGeneration++; }
        if (_folderOperation.IsCompleted) _folderBusy = false;
        RenderActiveDetails(); RenderHistoryDetails(); RenderHealth();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        InvalidateOwnerDelivery(closing: false); base.OnHandleDestroyed(e);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); ConstrainToWorkingArea(); }
    internal static Size MinimumForWorkingArea(Size area, int dpi) => new(Math.Min(area.Width, (int)Math.Ceiling(580d * dpi / 96)), Math.Min(area.Height, (int)Math.Ceiling(420d * dpi / 96)));
    private void ConstrainToWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea; MinimumSize = MinimumForWorkingArea(area.Size, DeviceDpi);
        Size = new(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
    }
    private UnifiedHistoryFilters CurrentFilters() => ObservationFormatting.Filters(Sources[Math.Max(0, _source.SelectedIndex)], (ObservationTimeRange)Math.Max(0, _range.SelectedIndex), _now(), _zone,
        _source.SelectedIndex == 1 && _status.SelectedIndex > 0 ? OrdinaryRecordingHistoryStatusCodes.Supported[_status.SelectedIndex - 1] : null);
    private void FiltersChanged(object? sender, EventArgs e)
    {
        if (_updatingLanguage || _closed) return;
        if (ReferenceEquals(sender, _source))
        {
            _updatingLanguage = true; _status.SelectedIndex = 0; _status.Enabled = _source.SelectedIndex == 1; _updatingLanguage = false;
        }
        _history.Reload(CurrentFilters()); RenderHistory();
    }
    private ObservationActiveRecording? SelectedActive() => _activeGrid.SelectedRows.Count == 1 ? _activeGrid.SelectedRows[0].Tag as ObservationActiveRecording : null;
    private UnifiedHistoryEntry? SelectedHistory() => _history.View.Status == ObservationHistoryStatus.Ready && _historyGrid.SelectedRows.Count == 1 ? _historyGrid.SelectedRows[0].Tag as UnifiedHistoryEntry : null;
    private void RefreshActive()
    {
        if (_closed) return;
        var selected = SelectedActive()?.Presentation.RecordingId;
        var rows = _active.GetObservationSnapshot();
        var sameIds = _activeGrid.Rows.Count == rows.Count && _activeGrid.Rows.Cast<DataGridViewRow>()
            .Select(r => (r.Tag as ObservationActiveRecording)?.Presentation.RecordingId).SequenceEqual(rows.Select(r => r.Presentation.RecordingId));
        _refreshingActive = true;
        if (!sameIds) _activeGrid.Rows.Clear();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var p = row.Presentation; var elapsed = "—";
            if (p.State == RecordingUiState.Recording && p.StartedAtUtc != default)
                elapsed = TimeSpan.FromSeconds(Math.Max(0, (_now().UtcDateTime - p.StartedAtUtc).TotalSeconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            var target = (p.IsScreenshotSeries ? _text.Get("Observation_ScreenshotSeries") : p.SourceType) + (p.NestedRole is null ? "" : " · " + p.NestedRole);
            object[] values = { p.RecordingId, target, ObservationFormatting.Phase(p, _text), elapsed };
            if (!sameIds) _activeGrid.Rows.Add(values);
            else for (var column = 0; column < values.Length; column++)
                if (!Equals(_activeGrid.Rows[index].Cells[column].Value, values[column])) _activeGrid.Rows[index].Cells[column].Value = values[column];
            _activeGrid.Rows[index].Tag = row;
        }
        if (!sameIds) RestoreSelection(_activeGrid, r => (r.Tag as ObservationActiveRecording)?.Presentation.RecordingId == selected);
        _refreshingActive = false;
        if (SelectedActive()?.Presentation.RecordingId != selected) { _selectionGeneration++; _stopMessage = null; }
        _activeStatus.Text = rows.Count == 0 ? _text.Get("Observation_ActiveEmpty") : _stopMessage ?? "";
        RenderActiveDetails();
    }
    private void RenderActiveDetails()
    {
        if (_closed) return;
        var row = SelectedActive(); SetDetails(_activeDetails, row is null ? "" : ObservationFormatting.ActiveDetails(row, _text, _zone));
        _stopButton.Enabled = row is { CanStop: true } && !_stop.IsBusy;
        _copyActive.Enabled = row is not null;
    }
    private void RenderHistory()
    {
        if (_closed) return;
        var view = _history.View;
        // A pending next page retains the displayed rows. Preserve their selected
        // identity during translation, while SelectedHistory still gates actions.
        var selected = _historyGrid.SelectedRows.Count == 1 ? (_historyGrid.SelectedRows[0].Tag as UnifiedHistoryEntry)?.EntryId : null;
        _historyGrid.Rows.Clear();
        foreach (var entry in view.Items)
        {
            var index = _historyGrid.Rows.Add(_text.Get("Observation_Source_" + entry.Source) + " / " + _text.Get("Observation_Kind_" + entry.EntryKind),
                ObservationFormatting.Time(entry.CreatedAtUtc, _zone), ObservationFormatting.State(entry.Status, _text), entry.EntryId, ObservationFormatting.Media(entry, _text));
            _historyGrid.Rows[index].Tag = entry;
        }
        RestoreSelection(_historyGrid, r => (r.Tag as UnifiedHistoryEntry)?.EntryId == selected);
        _historyStatus.Text = view.Status switch
        {
            ObservationHistoryStatus.Loading => _text.Get("Observation_Loading"), ObservationHistoryStatus.Empty => _text.Get("Observation_Empty"),
            ObservationHistoryStatus.InvalidCursor => _text.Get("Observation_InvalidCursor"), ObservationHistoryStatus.Unavailable => _text.Get("Observation_Unavailable"),
            ObservationHistoryStatus.Ready => _text.Format(view.NextCursor is null ? "Observation_PageEnd" : "Observation_PageMore", view.Items.Count), _ => ""
        };
        _next.Enabled = view.Status == ObservationHistoryStatus.Ready && view.NextCursor is not null;
        _timeNote.Text = view.Filters.CreatedFrom is { } from && view.Filters.CreatedBefore is { } before
            ? _text.Format("Observation_TimeBounds", ObservationFormatting.Time(from, _zone), ObservationFormatting.Time(before, _zone))
            : _text.Format("Observation_LocalTime", _zone.Id);
        RenderHistoryDetails();
    }
    private void RenderHistoryDetails()
    {
        if (_closed) return;
        var item = SelectedHistory(); SetDetails(_historyDetails, item is null ? "" : ObservationFormatting.HistoryDetails(item, _text, _zone));
        _copyId.Enabled = _copyDetails.Enabled = item is not null;
        _copyPath.Enabled = ObservationOutputAction.HasRecordedPath(item);
        _folder.Enabled = (!_folderBusy || _folderOperation.IsCompleted) && ObservationOutputAction.HasRecordedPath(item) && item!.OutputFileExists == true;
    }
    private Task StartStopOperation() => _stopOperation.IsCompleted
        ? _stopOperation = StopSelectedAsync() : _stopOperation;
    private Task StartFolderOperation() => _folderOperation.IsCompleted
        ? _folderOperation = OpenSelectedFolderAsync() : _folderOperation;
    private async Task StopSelectedAsync()
    {
        var selected = SelectedActive(); if (_closed || selected is null) return;
        var id = selected.Presentation.RecordingId; var generation = _selectionGeneration; var tab = _tabs.SelectedTab; var handleGeneration = _handleGeneration;
        _stopMessage = _text.Get("Observation_StopPending"); _activeStatus.Text = _stopMessage;
        var task = _stop.StopAsync(id); RenderActiveDetails();
        var result = await task.ConfigureAwait(false);
        await CompleteOnOwnerAsync(handleGeneration, () =>
        {
            if (generation == _selectionGeneration && _tabs.SelectedTab == tab && SelectedActive()?.Presentation.RecordingId == id)
            {
                _stopMessage = result.Outcome switch
                {
                    ObservationStopOutcome.Requested => _text.Get("Observation_StopRequested"), ObservationStopOutcome.NotStoppable => _text.Get("Observation_StopNotStoppable"),
                    ObservationStopOutcome.Busy => _text.Get("Observation_StopBusy"), ObservationStopOutcome.Failed => _text.Format("Observation_StopFailed", result.ErrorCode), _ => ""
                };
                _activeStatus.Text = _stopMessage; RefreshActive();
            }
            else RenderActiveDetails();
        }).ConfigureAwait(false);
    }
    private async Task OpenSelectedFolderAsync()
    {
        var item = SelectedHistory(); if (_closed || item is null || !_folder.Enabled) return;
        var revision = _history.View.Revision; var generation = _historySelectionGeneration; var tab = _tabs.SelectedTab; var handleGeneration = _handleGeneration;
        _folderBusy = true; _folder.Enabled = false;
        var opened = await Task.Run(() => ObservationOutputAction.OpenFolder(item, _desktop)).ConfigureAwait(false);
        await CompleteOnOwnerAsync(handleGeneration, () =>
        {
            _folderBusy = false;
            if (_history.View.Revision == revision && generation == _historySelectionGeneration && _tabs.SelectedTab == tab && SelectedHistory()?.EntryId == item.EntryId)
                _historyStatus.Text = _text.Get(opened ? "Observation_FolderOpened" : "Observation_FolderUnavailable");
            RenderHistoryDetails();
        }).ConfigureAwait(false);
    }
    private void Copy(string value, Label status)
    {
        if (_closed || string.IsNullOrEmpty(value)) return;
        try { _desktop.Copy(value); status.Text = _text.Get("Observation_Copied"); }
        catch { status.Text = _text.Get("Observation_CopyFailed"); }
    }
    private static void RestoreSelection(DataGridView grid, Func<DataGridViewRow, bool> match)
    {
        grid.ClearSelection(); var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(match) ?? grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (row is not null) row.Selected = true;
    }
    private static Button Button(string name) => new() { Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3, 4, 6, 4) };
    private static Label Label(string name) => new() { Name = name, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 6, 4) };
    private static TextBox Details(string name) => new() { Name = name, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, BackColor = SystemColors.Window };
    private static void SetDetails(TextBox box, string value)
    {
        if (box.Text == value) return;
        var start = box.SelectionStart; var length = box.SelectionLength; box.Text = value;
        box.Select(Math.Min(start, value.Length), Math.Min(length, Math.Max(0, value.Length - start)));
    }
    private static DataGridView Grid(string name) => new()
    {
        Name = name, Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, AccessibleName = name
    };
    private static DataGridViewTextBoxColumn Column(string name, int weight) => new() { Name = name, FillWeight = weight, MinimumWidth = 60, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static TableLayoutPanel Table(int rows) => new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) } };
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        flow.Controls.AddRange(controls); return flow;
    }
    private void ReleaseOwnResources()
    {
        if (_closed) return;
        _closed = true; _healthLifetime.Cancel(); _healthLifetime.Dispose(); InvalidateOwnerDelivery(closing: true); _activeTimer.Stop(); _activeTimer.Dispose(); _history.Changed -= RenderHistory; _history.Dispose(); _stop.Dispose();
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { ReleaseOwnResources(); base.OnFormClosed(e); }
    protected override void Dispose(bool disposing) { if (disposing) ReleaseOwnResources(); base.Dispose(disposing); }
}
