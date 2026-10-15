using System.Collections.Concurrent;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

internal sealed class OriginalFileHoldFixture
{
    internal const string Root = @"C:\host\Videos";
    internal PublicationIdentityAnchor Anchor = new(Root + @"\视频.mp4", 12, new(0x123456789abcdef0, 0xfedcba9876543210, 0x876543210abcdef0));
    internal readonly Native Api = new();
    internal readonly Paths Metadata = new();
    internal readonly OriginalFileHoldAdmission Admission = new();
    internal OriginalFileHoldService Service(string? root = null, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? deadline = null, Func<Action, Task>? dispatch = null)
    {
        Api.Path = Anchor.Path; Api.Id = Anchor.Identity;
        return new(root ?? Root, Api, Metadata, Admission, clock, deadline, dispatch);
    }
    internal sealed class Paths : IReadOnlyPathMetadata
    {
        internal ConcurrentQueue<string> Calls = new();
        internal Func<string, FileAttributes> Read = _ => FileAttributes.Directory;
        public FileAttributes GetAttributes(string path) { Calls.Enqueue(path); return Read(path); }
        public long GetLength(string path) => throw new InvalidOperationException("No path-length/data fallback");
    }
    internal sealed class Native : IOriginalFileHoldNative
    {
        internal ConcurrentQueue<string> Calls = new();
        internal List<SafeFileHandle> Opened = new();
        internal ConcurrentQueue<SafeFileHandle> Observed = new();
        internal Action<string>? Before;
        internal Func<int, FileBasicInformation>? BasicValue;
        internal Func<int, FileStandardInformation>? StandardValue;
        internal Func<int, FileIdentityInformation>? IdentityValue;
        internal Func<int, string>? PathValue;
        internal OriginalFileCloseOutcome CloseOutcome = OriginalFileCloseOutcome.Closed;
        internal bool Platform = true, InvalidOpen, ClosedOpen, ThrowOpen, ThrowClose, PretendClosed, FailBasic, FailStandard, FailIdentity;
        internal uint Drive = 3; internal int OpenError = 32; internal uint? PathLength;
        internal string Path = Root + @"\视频.mp4"; internal ObservedFileIdentity Id = new(1, 2, 3);
        internal uint Access, Share, Creation, Flags; internal string? OpenPath; internal int BasicCalls, StandardCalls, IdentityCalls, PathCalls, CloseCalls;
        private void Call(string name) { Calls.Enqueue(name); Before?.Invoke(name); }
        public bool Supported { get { Call("supported"); return Platform; } }
        public uint DriveType(string root) { Call("drive"); return Drive; }
        public SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags, out int error)
        {
            Call("open"); if (ThrowOpen) throw new IOException("private-open");
            OpenPath = path; Access = access; Share = share; Creation = creation; Flags = flags; error = OpenError;
            var handle = new SafeFileHandle(new IntPtr(InvalidOpen ? -1 : 123 + Opened.Count), ownsHandle: false); Opened.Add(handle);
            if (ClosedOpen) handle.Dispose(); return handle;
        }
        public bool Basic(SafeFileHandle handle, out FileBasicInformation value, out int error)
        {
            Observed.Enqueue(handle); Call("basic"); var i = Interlocked.Increment(ref BasicCalls); error = 0;
            value = BasicValue?.Invoke(i) ?? new() { Attributes = (uint)FileAttributes.Normal, CreationTime = 133000000000000000,
                LastAccessTime = 133000000000000000, LastWriteTime = 133000000000000000, ChangeTime = 133000000000000000 }; return !FailBasic;
        }
        public bool Standard(SafeFileHandle handle, out FileStandardInformation value, out int error)
        {
            Observed.Enqueue(handle); Call("standard"); var i = Interlocked.Increment(ref StandardCalls); error = 0;
            value = StandardValue?.Invoke(i) ?? new() { EndOfFile = 12, AllocationSize = 4096, NumberOfLinks = 1 }; return !FailStandard;
        }
        public bool Identity(SafeFileHandle handle, out FileIdentityInformation value, out int error)
        {
            Observed.Enqueue(handle); Call("identity"); var i = Interlocked.Increment(ref IdentityCalls); error = 0;
            value = IdentityValue?.Invoke(i) ?? new() { VolumeSerialNumber = Id.VolumeSerialNumber, IdLow = Id.IdLow, IdHigh = Id.IdHigh }; return !FailIdentity;
        }
        public uint FinalPath(SafeFileHandle handle, StringBuilder buffer, int capacity, out int error)
        {
            Observed.Enqueue(handle); Call("path"); var i = Interlocked.Increment(ref PathCalls); error = 0;
            buffer.Append(PathValue?.Invoke(i) ?? @"\\?\" + Path); return PathLength ?? (uint)buffer.Length;
        }
        public OriginalFileCloseOutcome Close(SafeFileHandle handle)
        {
            Observed.Enqueue(handle); Interlocked.Increment(ref CloseCalls); Call("close");
            if (PretendClosed) handle.Dispose(); if (ThrowClose) throw new IOException("private-close");
            if (CloseOutcome == OriginalFileCloseOutcome.Closed) handle.Dispose(); return CloseOutcome;
        }
    }
    internal sealed class Phase : IDisposable
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        internal void Block() { Entered.TrySetResult(); Assert.True(_release.Wait(TimeSpan.FromSeconds(15))); }
        internal void Release() => _release.Set();
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }
    internal sealed class Clock : TimeProvider
    {
        internal long Tick;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref Tick);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref Tick, duration.Ticks);
    }
    internal static async Task Retire(OriginalFileHoldOwner owner)
    { owner.Dispose(); Assert.True((await owner.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); }
}
