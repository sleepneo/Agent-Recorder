using System.Collections.Concurrent;
using System.Text;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentRecorder.Tests;

internal sealed class OriginalFileScopeFixture
{
    internal string Root = OriginalFileHoldFixture.Root;
    internal PublicationIdentityAnchor Anchor = new(OriginalFileHoldFixture.Root + @"\视频.mp4", 12, new(123, 456, 789));
    internal readonly Native Api = new();
    internal readonly OriginalFileHoldAdmission Admission = new();
    internal OriginalFileScopeHoldService Service(TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? deadline = null, Func<Action, Task>? dispatch = null)
    { Api.Anchor = Anchor; return new(Root, Api, Admission, clock, deadline, dispatch); }
    internal sealed record Call(string Stage, int Index, int Number, SafeFileHandle? Handle);
    internal sealed class Node(int index, string path, bool leaf, SafeFileHandle handle)
    {
        internal readonly int Index = index;
        internal readonly string Path = path;
        internal readonly bool Leaf = leaf;
        internal readonly SafeFileHandle Handle = handle;
    }
    internal sealed class Native : IOriginalFileHoldNative
    {
        internal PublicationIdentityAnchor Anchor = null!;
        internal readonly ConcurrentQueue<Node> Opened = new();
        internal readonly ConcurrentQueue<Call> Calls = new();
        private readonly ConcurrentDictionary<SafeFileHandle, Node> _nodes = new();
        private readonly ConcurrentDictionary<(string, int), int> _counts = new();
        internal Action<Call>? Before;
        internal Func<Call, bool>? Fail;
        internal Func<Node, int, FileBasicInformation, FileBasicInformation>? BasicValue;
        internal Func<Node, int, FileStandardInformation, FileStandardInformation>? StandardValue;
        internal Func<Node, int, FileIdentityInformation, FileIdentityInformation>? IdentityValue;
        internal Func<Node, int, string, string>? PathValue;
        internal Func<Node, OriginalFileCloseOutcome>? CloseValue;
        internal int? NullOpen, ClosedOpen;
        internal bool Platform = true;
        internal uint Drive = 3;
        internal readonly ConcurrentQueue<(string Path, uint Access, uint Share, uint Creation, uint Flags)> Parameters = new();
        private Call Enter(string stage, int index, SafeFileHandle? handle = null)
        {
            var call = new Call(stage, index, _counts.AddOrUpdate((stage, index), 1, (_, n) => n + 1), handle);
            Calls.Enqueue(call); Before?.Invoke(call); return call;
        }
        public bool Supported { get { Enter("supported", -1); return Platform; } }
        public uint DriveType(string root) { Enter("drive", -1); return Drive; }
        public SafeFileHandle Open(string path, uint access, uint share, uint creation, uint flags, out int error)
        {
            var index = Opened.Count; var call = Enter("open", index); error = 32;
            Parameters.Enqueue((path, access, share, creation, flags));
            if (NullOpen == index) return null!;
            var handle = new SafeFileHandle(new IntPtr(Fail?.Invoke(call) == true ? -1 : 1000 + index), false);
            var node = new Node(index, path[4..], (flags & 0x02000000) == 0, handle);
            Opened.Enqueue(node); _nodes[handle] = node;
            if (ClosedOpen == index) handle.Dispose(); return handle;
        }
        public bool Basic(SafeFileHandle handle, out FileBasicInformation value, out int error)
        {
            var n = _nodes[handle]; var c = Enter("basic", n.Index, handle); error = 0;
            value = new() { Attributes = (uint)(n.Leaf ? FileAttributes.Normal : FileAttributes.Directory), LastWriteTime = 133000000000000000 };
            value = BasicValue?.Invoke(n, c.Number, value) ?? value; return Fail?.Invoke(c) != true;
        }
        public bool Standard(SafeFileHandle handle, out FileStandardInformation value, out int error)
        {
            var n = _nodes[handle]; var c = Enter("standard", n.Index, handle); error = 0;
            value = new() { EndOfFile = n.Leaf ? 12 : 0, AllocationSize = n.Leaf ? 4096 : 0, NumberOfLinks = n.Leaf ? 1u : 0u, Directory = n.Leaf ? (byte)0 : (byte)1 };
            value = StandardValue?.Invoke(n, c.Number, value) ?? value; return Fail?.Invoke(c) != true;
        }
        public bool Identity(SafeFileHandle handle, out FileIdentityInformation value, out int error)
        {
            var n = _nodes[handle]; var c = Enter("identity", n.Index, handle); error = 0;
            value = new() { VolumeSerialNumber = Anchor.Identity.VolumeSerialNumber, IdLow = n.Leaf ? Anchor.Identity.IdLow : (ulong)n.Index + 100,
                IdHigh = n.Leaf ? Anchor.Identity.IdHigh : 900 };
            value = IdentityValue?.Invoke(n, c.Number, value) ?? value; return Fail?.Invoke(c) != true;
        }
        public uint FinalPath(SafeFileHandle handle, StringBuilder buffer, int capacity, out int error)
        {
            var n = _nodes[handle]; var c = Enter("path", n.Index, handle); error = 0;
            buffer.Append(PathValue?.Invoke(n, c.Number, @"\\?\" + n.Path) ?? @"\\?\" + n.Path);
            return Fail?.Invoke(c) == true ? 0 : (uint)buffer.Length;
        }
        public OriginalFileCloseOutcome Close(SafeFileHandle handle)
        {
            var n = _nodes[handle]; Enter("close", n.Index, handle);
            var result = CloseValue?.Invoke(n) ?? OriginalFileCloseOutcome.Closed;
            if (result == OriginalFileCloseOutcome.Closed) handle.Dispose(); return result;
        }
    }
    internal sealed class Deadline
    {
        internal readonly TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Arms;
        internal Task Arm(TimeSpan duration, CancellationToken token)
        { Assert.Equal(TimeSpan.FromSeconds(3), duration); Arms++; token.Register(() => Signal.TrySetCanceled(token)); return Signal.Task; }
    }
    internal static async Task Retire(OriginalFileScopeOwner owner)
    { owner.Dispose(); Assert.True((await owner.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); }
    internal static void AssertClosedInReverse(Native api)
    {
        var nodes = api.Opened.Where(n => !n.Handle.IsInvalid).Reverse().ToArray();
        var closes = api.Calls.Where(c => c.Stage == "close").ToArray();
        Assert.Equal(nodes.Select(n => n.Index), closes.Select(c => c.Index));
        for (var i = 0; i < nodes.Length; i++) Assert.Same(nodes[i].Handle, closes[i].Handle);
    }
}
