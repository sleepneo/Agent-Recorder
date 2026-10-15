using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Infrastructure;

internal enum OriginalFileHoldReason
{
    Held, Observed, InvalidInput, Busy, Cancelled, TimedOut, Unsupported,
    UnsupportedDrive, AncestorUnavailable, AncestorUnsafe, Missing, Inaccessible,
    SharingViolation, OpenFailed, MetadataUnavailable, StructureInvalid,
    IdentityMismatch, LengthMismatch, PathMismatch, MetadataChanged, WorkerFailed, Disposed
}
internal sealed record OriginalFileHoldObservation(OriginalFileHoldReason Reason, HandleMetadataSnapshot? Snapshot = null);
internal sealed record OriginalFileHoldRetirement(bool Retired, OriginalFileHoldReason? PrimaryReason, OriginalFileCloseOutcome Cleanup);
internal sealed record OriginalFileHoldResult(OriginalFileHoldReason Reason, OriginalFileHoldReason? PrimaryReason,
    OriginalFileHoldOwner? Owner, Task<OriginalFileHoldRetirement> Retirement);
internal enum OriginalFileBorrowReason { Borrowed, InvalidInput, Disposed, Busy, RecheckFailed, Cancelled, CallbackFailed, WorkerFailed }
internal enum OriginalFileBorrowContinuationResult { Completed }
internal sealed record OriginalFileBorrowResult(OriginalFileBorrowReason Reason, OriginalFileHoldReason? Observation = null);

// No handle, snapshot, asynchronous operation or independent construction exit.
internal sealed class OriginalFileBorrowContext
{
    private readonly OriginalFileHoldService.Session _session;
    private readonly Action _check;
    private readonly int _thread;
    private int _active = 1;
    private OriginalFileBorrowContext(OriginalFileHoldService.Session session, Action check)
    { _session = session; _check = check; _thread = Environment.CurrentManagedThreadId; }
    internal void RequireActive()
    {
        if (Volatile.Read(ref _active) == 0 || _thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("The original-object borrow has expired.");
        _session.CheckBorrowUse(); _check();
    }
    internal static OriginalFileBorrowResult Invoke(OriginalFileHoldService.Session session, Action check,
        Func<OriginalFileBorrowContext, OriginalFileBorrowContinuationResult> continuation)
    {
        session.CheckBorrowUse();
        var context = new OriginalFileBorrowContext(session, check);
        try
        {
            context.RequireActive();
            try
            {
                if (continuation(context) != OriginalFileBorrowContinuationResult.Completed)
                    return new(OriginalFileBorrowReason.CallbackFailed);
            }
            catch (OperationCanceledException) { return new(OriginalFileBorrowReason.Cancelled); }
            catch { return new(OriginalFileBorrowReason.CallbackFailed); }
            context.RequireActive();
            return new(OriginalFileBorrowReason.Borrowed);
        }
        finally { Volatile.Write(ref context._active, 0); }
    }
    public override string ToString() => "OriginalObjectBorrow(current-only)";

    // Dependency inversion only: the private concrete implementation lives in
    // the consumed local attempt. Pure borrow callers cannot construct it, and
    // this context has no general Delete method or raw-handle exit.
    internal abstract class DispositionAuthorization
    {
        private protected DispositionAuthorization() { }
        internal abstract bool TryBegin(OriginalFileBorrowContext current);
        internal abstract void Complete(OriginalFileDispositionResult result);
        internal void Submit(OriginalFileBorrowContext current)
        {
            current.RequireActive();
            current._session.SubmitDisposition(current, this);
        }
    }
}

// One actual acquiring/held/observing/closing lifecycle per host process. Tests
// inject isolated admissions; independent production service instances share it.
internal sealed class OriginalFileHoldAdmission
{
    internal static OriginalFileHoldAdmission Host { get; } = new();
    private readonly object _sync = new();
    private object? _active;
    internal bool Claim(object owner) { lock (_sync) { if (_active is not null) return false; _active = owner; return true; } }
    internal void Release(object owner) { lock (_sync) { if (!ReferenceEquals(_active, owner)) throw new InvalidOperationException(); _active = null; } }
}

// Internal host-frozen root + previously trusted publication data only. There is
// no HTTP/CLI caller. Disposition requires a consumed local attempt capability.
internal sealed class OriginalFileHoldService
{
    internal const uint Access = 0x00010000 | WindowsReadOnlyFileMetadataProbe.ReadAttributes; // DELETE + FILE_READ_ATTRIBUTES
    internal const uint Sharing = 0;
    internal const uint Flags = 0x00200000; // OPEN_REPARSE_POINT, never DELETE_ON_CLOSE
    internal static readonly TimeSpan AcquisitionBudget = TimeSpan.FromSeconds(3);
    internal const int MaxAncestors = 32;
    private readonly string _root;
    private readonly IOriginalFileHoldNative _native;
    private readonly IReadOnlyPathMetadata _paths;
    private readonly OriginalFileHoldAdmission _admission;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _deadline;
    private readonly Func<Action, Task> _dispatch;

    internal OriginalFileHoldService(string frozenVideosRoot, IOriginalFileHoldNative? native = null,
        IReadOnlyPathMetadata? paths = null, OriginalFileHoldAdmission? admission = null, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? deadline = null, Func<Action, Task>? dispatch = null)
    {
        _root = frozenVideosRoot; _native = native ?? WindowsOriginalFileHoldNative.Instance;
        _paths = paths ?? ReadOnlyPathMetadata.Instance; _admission = admission ?? OriginalFileHoldAdmission.Host;
        _clock = clock ?? TimeProvider.System;
        _deadline = deadline ?? ((duration, token) => Task.Delay(duration, _clock, token));
        _dispatch = dispatch ?? (work => Task.Run(work));
    }

    internal Task<OriginalFileHoldResult> AcquireAsync(PublicationIdentityAnchor anchor, CancellationToken cancellation = default)
        => AcquireCore(anchor, null, cancellation);

    // Scope acquisition uses this same Session/admission/deadline, not a second
    // leaf request. Default Task353 callers keep their original path observer.
    internal Task<OriginalFileHoldResult> AcquireScopeAsync(PublicationIdentityAnchor anchor,
        OriginalFileScopeResources scope, CancellationToken cancellation, long? started = null)
        => AcquireCore(anchor, scope, cancellation, started);

    internal static bool ValidScopeInput(string root, PublicationIdentityAnchor anchor)
        => anchor is not null && anchor.Valid && Scoped(root, anchor.Path, out _);

    private Task<OriginalFileHoldResult> AcquireCore(PublicationIdentityAnchor anchor,
        OriginalFileScopeResources? scope, CancellationToken cancellation, long? absoluteStarted = null)
    {
        var started = absoluteStarted ?? _clock.GetTimestamp(); // before dispatch, including failure cleanup
        if (anchor is null || !anchor.Valid || !Scoped(_root, anchor.Path, out var ancestors)) return Rejected(OriginalFileHoldReason.InvalidInput);
        if (cancellation.IsCancellationRequested) return Rejected(OriginalFileHoldReason.Cancelled);
        if (absoluteStarted.HasValue && _clock.GetElapsedTime(started) >= AcquisitionBudget) return Rejected(OriginalFileHoldReason.TimedOut);
        var session = new Session(this, anchor, ancestors!, started, cancellation, scope, absoluteStarted.HasValue);
        if (!_admission.Claim(session)) return Rejected(OriginalFileHoldReason.Busy);
        return session.Start();
    }

    private static Task<OriginalFileHoldResult> Rejected(OriginalFileHoldReason reason) => Task.FromResult(new OriginalFileHoldResult(
        reason, reason, null, Task.FromResult(new OriginalFileHoldRetirement(true, reason, OriginalFileCloseOutcome.NotNeeded))));

    private static bool Scoped(string root, string path, out string[]? ancestors)
    {
        ancestors = null;
        if (!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(root) || !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(path)) return false;
        var rootParts = root[3..].Split('\\'); var parts = path[3..].Split('\\');
        if (!rootParts[^1].Equals("Videos", StringComparison.OrdinalIgnoreCase) || parts.Length != rootParts.Length + 1 ||
            rootParts.Length + 1 > MaxAncestors || !string.Equals(path[..path.LastIndexOf('\\')], root, StringComparison.OrdinalIgnoreCase) ||
            !parts.All(Component) || !rootParts.All(Component) || parts[^1].Length <= 4 || !parts[^1].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        var checks = new List<string> { root[..3] }; var current = root[..3];
        foreach (var part in rootParts) { current = current.EndsWith('\\') ? current + part : current + "\\" + part; checks.Add(current); }
        ancestors = checks.ToArray(); return true;
    }
    private static bool Component(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (stem[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3'));
    }

    internal sealed class Session
    {
        private readonly OriginalFileHoldService _service;
        private readonly PublicationIdentityAnchor _anchor;
        private readonly string[] _ancestors;
        private readonly long _started;
        private readonly CancellationToken _cancellation;
        private readonly OriginalFileScopeResources? _scope;
        private readonly bool _absoluteDeadline;
        private readonly object _sync = new();
        private readonly TaskCompletionSource<OriginalFileHoldResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<OriginalFileHoldRetirement> _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SafeFileHandle? _handle;
        private bool _openStarted, _openUnknown, _published, _observing, _closeStarted;
        private volatile bool _abandoned, _disposeRequested;
        private OriginalFileHoldReason? _primary;
        private Action? _borrowCheck;
        private int _borrowThread;
        private int _dispositionStarted;
        internal Session(OriginalFileHoldService service, PublicationIdentityAnchor anchor, string[] ancestors, long started, CancellationToken cancellation,
            OriginalFileScopeResources? scope = null, bool absoluteDeadline = false)
        { _service = service; _anchor = anchor; _ancestors = ancestors; _started = started; _cancellation = cancellation; _scope = scope; _absoluteDeadline = absoluteDeadline; }
        internal Task<OriginalFileHoldRetirement> Retirement => _retirement.Task;

        internal async Task<OriginalFileHoldResult> Start()
        {
            using var stopDeadline = new CancellationTokenSource();
            using var registration = _cancellation.Register(() => Freeze(OriginalFileHoldReason.Cancelled));
            try
            {
                var budget = _absoluteDeadline ? AcquisitionBudget - _service._clock.GetElapsedTime(_started) : AcquisitionBudget;
                if (budget <= TimeSpan.Zero) { Freeze(OriginalFileHoldReason.TimedOut); FinishRetirement(OriginalFileCloseOutcome.NotNeeded); return await _result.Task.ConfigureAwait(false); }
                var deadline = _service._deadline(budget, stopDeadline.Token);
                _ = WatchDeadline(deadline, stopDeadline.Token);
                Task worker;
                try { worker = _service._dispatch(AcquireNative); }
                catch { worker = Task.FromException(new InvalidOperationException()); }
                _ = FinishAcquisition(worker);
            }
            catch { SetPrimary(OriginalFileHoldReason.WorkerFailed); FinishRetirement(OriginalFileCloseOutcome.NotNeeded); }
            try { return await _result.Task.ConfigureAwait(false); }
            finally { stopDeadline.Cancel(); }
        }
        private async Task WatchDeadline(Task deadline, CancellationToken stopped)
        {
            try { await deadline.ConfigureAwait(false); if (!stopped.IsCancellationRequested) Freeze(OriginalFileHoldReason.TimedOut); }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            catch { if (!stopped.IsCancellationRequested) Freeze(OriginalFileHoldReason.WorkerFailed); }
        }
        private void Freeze(OriginalFileHoldReason reason)
        {
            lock (_sync)
            {
                if (_published || _result.Task.IsCompleted) return;
                _abandoned = true; _primary ??= reason;
                _result.TrySetResult(new(reason, _primary, null, Retirement));
            }
        }
        private void SetPrimary(OriginalFileHoldReason reason) { lock (_sync) _primary ??= reason; }
        private void CheckStop()
        {
            if (_abandoned || _disposeRequested || !_published && _cancellation.IsCancellationRequested) throw new OperationCanceledException();
            _borrowCheck?.Invoke();
            if (_scope is not null && !_published && _service._clock.GetElapsedTime(_started) >= AcquisitionBudget)
            { Freeze(OriginalFileHoldReason.TimedOut); throw new OperationCanceledException(); }
            if (_handle is { } handle && (handle.IsClosed || handle.IsInvalid)) throw new InvalidDataException();
        }
        private OriginalFileHoldObservation? _acquisition;
        private OriginalFileCloseOutcome _acquisitionCleanup;
        private void AcquireNative()
        {
            try
            {
                CheckStop();
                var scope = CheckAncestors();
                if (scope is not null) { SetPrimary(scope.Value); return; }
                CheckStop(); _openStarted = true;
                try { _handle = _service._native.Open(@"\\?\" + _anchor.Path, Access, Sharing, WindowsReadOnlyFileMetadataProbe.OpenExisting, Flags, out var error);
                    if (_handle is null) { _openUnknown = true; SetPrimary(OriginalFileHoldReason.OpenFailed); return; }
                    if (_handle.IsInvalid) { SetPrimary(error switch { 2 or 3 => OriginalFileHoldReason.Missing, 5 => OriginalFileHoldReason.Inaccessible,
                        32 or 33 => OriginalFileHoldReason.SharingViolation, _ => OriginalFileHoldReason.OpenFailed }); return; }
                    if (_handle.IsClosed) { _openUnknown = true; SetPrimary(OriginalFileHoldReason.OpenFailed); return; }
                }
                catch { _openUnknown = true; throw; }
                CheckStop(); _acquisition = ReadSample();
                if (_acquisition.Reason != OriginalFileHoldReason.Observed) SetPrimary(_acquisition.Reason);
            }
            catch (OperationCanceledException) { SetPrimary(_cancellation.IsCancellationRequested ? OriginalFileHoldReason.Cancelled : OriginalFileHoldReason.TimedOut); }
            catch { SetPrimary(OriginalFileHoldReason.WorkerFailed); }
            finally { if (_acquisition?.Reason != OriginalFileHoldReason.Observed) _acquisitionCleanup = CloseNative(); }
        }
        private async Task FinishAcquisition(Task worker)
        {
            try { await worker.ConfigureAwait(false); }
            catch { SetPrimary(OriginalFileHoldReason.WorkerFailed); _acquisition = null; _acquisitionCleanup = CloseNative(); }
            if (_acquisition?.Reason != OriginalFileHoldReason.Observed) { FinishRetirement(_acquisitionCleanup); return; }
            lock (_sync)
            {
                try
                {
                    if (!_abandoned && !_cancellation.IsCancellationRequested && _service._clock.GetElapsedTime(_started) < AcquisitionBudget)
                    {
                        _published = true; _result.TrySetResult(new(OriginalFileHoldReason.Held, null, new OriginalFileHoldOwner(this), Retirement)); return;
                    }
                }
                catch { _primary ??= OriginalFileHoldReason.WorkerFailed; _abandoned = true;
                    _result.TrySetResult(new(OriginalFileHoldReason.WorkerFailed, _primary, null, Retirement)); }
            }
            Freeze(_cancellation.IsCancellationRequested ? OriginalFileHoldReason.Cancelled : OriginalFileHoldReason.TimedOut);
            RequestDispose(); // late success is never handed off
        }
        private OriginalFileHoldReason? CheckAncestors()
        {
            CheckStop(); if (!_service._native.Supported) return OriginalFileHoldReason.Unsupported;
            CheckStop(); if (_service._native.DriveType(_anchor.Path[..3]) != 3) return OriginalFileHoldReason.UnsupportedDrive;
            if (_scope is not null) return _openStarted ? _scope.Verify(CheckStop) : _scope.Open(_ancestors, _anchor.Identity, CheckStop);
            foreach (var path in _ancestors)
            {
                CheckStop(); FileAttributes attributes;
                try { attributes = _service._paths.GetAttributes(path); }
                catch { return OriginalFileHoldReason.AncestorUnavailable; }
                if (!WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)attributes) ||
                    (attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) return OriginalFileHoldReason.AncestorUnsafe;
            }
            return null;
        }
        private OriginalFileHoldObservation ReadSample()
        {
            CheckStop();
            var scope = _scope?.Verify(CheckStop);
            if (scope is not null) return new(scope.Value);
            if (!ReadMetadata(out var firstBasic, out var firstStandard, out var firstIdentity)) return new(OriginalFileHoldReason.MetadataUnavailable);
            var first = Snapshot(firstBasic, firstStandard, firstIdentity);
            if (!Structure(first)) return new(OriginalFileHoldReason.StructureInvalid);
            CheckStop(); var firstPath = FinalPath(); if (firstPath is null) return new(OriginalFileHoldReason.PathMismatch);
            CheckStop();
            if (!ReadMetadata(out var secondBasic, out var secondStandard, out var secondIdentity)) return new(OriginalFileHoldReason.MetadataUnavailable);
            var second = Snapshot(secondBasic, secondStandard, secondIdentity);
            CheckStop(); var secondPath = FinalPath(); if (secondPath is null) return new(OriginalFileHoldReason.PathMismatch);
            if (!Structure(second) || !firstBasic.Equals(secondBasic) || first != second || firstPath != secondPath) return new(OriginalFileHoldReason.MetadataChanged);
            if (first.Identity != _anchor.Identity) return new(OriginalFileHoldReason.IdentityMismatch);
            if (first.Length != _anchor.Length) return new(OriginalFileHoldReason.LengthMismatch);
            scope = _scope?.Verify(CheckStop);
            if (scope is not null) return new(scope.Value);
            CheckStop(); return new(OriginalFileHoldReason.Observed, first);
        }
        private bool ReadMetadata(out FileBasicInformation basic, out FileStandardInformation standard, out FileIdentityInformation identity)
        {
            basic = default; standard = default; identity = default;
            CheckStop(); if (!_service._native.Basic(_handle!, out basic, out _)) return false;
            CheckStop(); if (!_service._native.Standard(_handle!, out standard, out _)) return false;
            CheckStop(); return _service._native.Identity(_handle!, out identity, out _);
        }
        private static HandleMetadataSnapshot Snapshot(FileBasicInformation basic, FileStandardInformation standard, FileIdentityInformation identity)
            => new(standard.EndOfFile, standard.AllocationSize, standard.NumberOfLinks, (FileAttributes)basic.Attributes, standard.DeletePending,
                standard.Directory, basic.LastWriteTime, new(identity.VolumeSerialNumber, identity.IdLow, identity.IdHigh));
        private static bool Structure(HandleMetadataSnapshot sample) => WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)sample.Attributes) &&
            (sample.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && sample.Length > 0 && sample.AllocationSize >= 0 &&
            sample.Links == 1 && sample.DeletePending == 0 && sample.Directory == 0 && sample.LastWriteTime > 0 &&
            sample.LastWriteTime <= 2650467743999999999 && sample.Identity is { VolumeSerialNumber: > 0 } identity && (identity.IdLow != 0 || identity.IdHigh != 0);
        private string? FinalPath()
        {
            var buffer = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
            var length = _service._native.FinalPath(_handle!, buffer, WindowsReadOnlyFileMetadataProbe.FinalPathCapacity, out _);
            if (length == 0 || length >= WindowsReadOnlyFileMetadataProbe.FinalPathCapacity || length != buffer.Length) return null;
            var path = buffer.ToString(); if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            return Scoped(_service._root, path, out _) && string.Equals(path, _anchor.Path, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        internal Task<OriginalFileHoldObservation> ObserveAsync()
        {
            lock (_sync)
            {
                if (_disposeRequested || !_published) return Task.FromResult(new OriginalFileHoldObservation(OriginalFileHoldReason.Disposed));
                if (_observing) return Task.FromResult(new OriginalFileHoldObservation(OriginalFileHoldReason.Busy));
                _observing = true;
            }
            return ObserveWorker();
        }
        internal void CheckBorrowUse()
        {
            if (_borrowThread != Environment.CurrentManagedThreadId || _borrowCheck is null)
                throw new InvalidOperationException("The original-object borrow is unavailable.");
            CheckStop();
        }
        internal OriginalFileBorrowResult Borrow(PublicationIdentityAnchor anchor, Action check,
            Func<OriginalFileBorrowContext, OriginalFileBorrowContinuationResult> continuation)
        {
            if (anchor != _anchor || check is null || continuation is null || _scope is null)
                return new(OriginalFileBorrowReason.InvalidInput);
            lock (_sync)
            {
                if (_disposeRequested || !_published) return new(OriginalFileBorrowReason.Disposed);
                if (_observing || _closeStarted) return new(OriginalFileBorrowReason.Busy);
                _observing = true; _borrowThread = Environment.CurrentManagedThreadId; _borrowCheck = check;
            }
            try
            {
                CheckBorrowUse();
                var observed = ReadSample(); // Reuse exact held directory/leaf sampling.
                if (observed.Reason != OriginalFileHoldReason.Observed)
                { SetPrimary(observed.Reason); return new(OriginalFileBorrowReason.RecheckFailed, observed.Reason); }
                return OriginalFileBorrowContext.Invoke(this, check, continuation);
            }
            catch (OperationCanceledException) { return new(OriginalFileBorrowReason.Cancelled); }
            catch { return new(OriginalFileBorrowReason.WorkerFailed); }
            finally
            {
                bool close;
                lock (_sync) { _borrowCheck = null; _borrowThread = 0; _observing = false; close = _disposeRequested; }
                if (close) RequestDispose(); // Actual synchronous use has now ended.
            }
        }
        internal void SubmitDisposition(OriginalFileBorrowContext current, OriginalFileBorrowContext.DispositionAuthorization authorization)
        {
            CheckBorrowUse();
            var observed = ReadSample(); // Fresh original leaf AND directory chain, on this borrow thread.
            if (observed.Reason != OriginalFileHoldReason.Observed)
            { SetPrimary(observed.Reason); throw new InvalidOperationException("Original-object recheck failed."); }
            current.RequireActive();
            if (Interlocked.CompareExchange(ref _dispositionStarted, 1, 0) != 0 || !authorization.TryBegin(current)) return;
            // No cancellation/metadata test between a successful native return
            // and recording that fact. Never reset FALSE or retry this handle.
            OriginalFileDispositionResult result;
            try { result = _service._native.RequestDisposition(_handle!); }
            catch { result = new(OriginalFileDispositionOutcome.Indeterminate); }
            if (result is null || !Enum.IsDefined(result.Outcome)) result = new(OriginalFileDispositionOutcome.Indeterminate);
            authorization.Complete(result);
        }
        private async Task<OriginalFileHoldObservation> ObserveWorker()
        {
            OriginalFileHoldObservation result;
            try { result = await Task.Run(() => { var scope = CheckAncestors(); return scope is null ? ReadSample() : new OriginalFileHoldObservation(scope.Value); }).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = new(OriginalFileHoldReason.Disposed); }
            catch { result = new(OriginalFileHoldReason.WorkerFailed); }
            bool close;
            lock (_sync)
            {
                _observing = false; close = _disposeRequested;
                if (close) result = new(OriginalFileHoldReason.Disposed);
                else if (result.Reason != OriginalFileHoldReason.Observed) _primary ??= result.Reason;
            }
            if (close) RequestDispose();
            return result;
        }
        internal void RequestDispose()
        {
            lock (_sync)
            {
                _disposeRequested = true;
                if (_observing || _closeStarted) return;
                _closeStarted = true;
            }
            _ = CloseWorker();
        }
        private async Task CloseWorker()
        {
            OriginalFileCloseOutcome cleanup;
            try { cleanup = await Task.Run(CloseNative).ConfigureAwait(false); }
            catch { cleanup = OriginalFileCloseOutcome.Unknown; }
            FinishRetirement(cleanup);
        }
        private OriginalFileCloseOutcome CloseNative()
        {
            var leaf = CloseLeafNative();
            return _scope is null ? leaf : _scope.CloseAfterLeaf(leaf, _openStarted);
        }
        private OriginalFileCloseOutcome CloseLeafNative()
        {
            if (!_openStarted) return OriginalFileCloseOutcome.NotNeeded;
            if (_openUnknown || _handle is null || _handle.IsClosed) return OriginalFileCloseOutcome.Unknown;
            if (_handle.IsInvalid) { _handle.Dispose(); return OriginalFileCloseOutcome.NotNeeded; }
            try { var outcome = _service._native.Close(_handle); return outcome is OriginalFileCloseOutcome.Closed or OriginalFileCloseOutcome.Failed ? outcome : OriginalFileCloseOutcome.Unknown; }
            catch { return OriginalFileCloseOutcome.Unknown; }
        }
        private void FinishRetirement(OriginalFileCloseOutcome cleanup)
        {
            lock (_sync)
            {
                var confirmed = cleanup is OriginalFileCloseOutcome.NotNeeded or OriginalFileCloseOutcome.Closed;
                if (confirmed) { _handle = null; _service._admission.Release(this); }
                // Failed/unknown ownership remains rooted by the admission. No
                // numeric-handle retry, inferred close, or hidden capacity release.
                _retirement.TrySetResult(new(confirmed, _primary, cleanup));
                if (!_result.Task.IsCompleted) _result.TrySetResult(new(_primary ?? OriginalFileHoldReason.WorkerFailed, _primary, null, Retirement));
            }
        }
    }
}

internal sealed class OriginalFileHoldOwner : IDisposable
{
    private readonly OriginalFileHoldService.Session _session;
    internal OriginalFileHoldOwner(OriginalFileHoldService.Session session) => _session = session;
    internal Task<OriginalFileHoldObservation> ObserveAsync() => _session.ObserveAsync();
    internal Task<OriginalFileHoldRetirement> Retirement => _session.Retirement;
    internal OriginalFileBorrowResult Borrow(PublicationIdentityAnchor anchor, Action check,
        Func<OriginalFileBorrowContext, OriginalFileBorrowContinuationResult> continuation)
        => _session.Borrow(anchor, check, continuation);
    // Nonblocking request. Retirement, rather than IsClosed/Dispose-requested,
    // reports the actual worker/close result. No engine/recording/UI lock is used.
    public void Dispose() => _session.RequestDispose();
}
