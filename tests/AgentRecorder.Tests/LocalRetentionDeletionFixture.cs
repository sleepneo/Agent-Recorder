using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

internal sealed class LocalRetentionDeletionFixture : IDisposable
{
    internal readonly UnifiedHistoryTestDatabase Db = new(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task362", Guid.NewGuid().ToString("N")));
    internal readonly RecordingEngine Engine;
    internal readonly OriginalFileScopeFixture Files = new();
    internal readonly LocalRetentionApprovalFixture.Ui Ui = new();
    internal readonly LocalRetentionApprovalFixture.Clock Clock = new();
    internal readonly LocalRetentionAdmission Admission = new();
    internal readonly RetentionGuardAdmission DatabaseAdmission = new();
    internal readonly RetentionComposedAdmission ComposedAdmission = new();
    internal readonly Adapter Native;
    internal readonly OrdinaryRecordingHistorySnapshot Row;
    internal PublicationIdentityAnchor Anchor => Row.PublicationReceipt!.Anchor;
    internal string Videos => Path.Combine(Db.Root, "Videos");
    internal string Media => Path.Combine(Videos, "本轮可丢弃占位.mp4");
    internal readonly byte[] Bytes = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();
    internal bool Removed, Replacement;
    internal string? PreservedOriginalPath;
    internal string? Preparation;
    private readonly bool _real;
    private readonly string? _mode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly string? _directory = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
    internal LocalRetentionDeletionFixture(bool real = false)
    {
        _real = real; Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); DataDirResolver.SetOverride(Db.Root);
        SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Synthetic", true, new(0,0,1920,1080), 1.0) });
        Engine = new(new RetentionSharedOutputPathTests.QuietAudit()); Engine.ConfigureOrdinaryRecordingHistory(Db.Ordinary);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task362")) + Path.DirectorySeparatorChar, Path.GetFullPath(Media), StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Videos); using (var stream = new FileStream(Media, FileMode.CreateNew, FileAccess.Write)) stream.Write(Bytes);
        var identity = real ? WindowsReadOnlyFileMetadataProbe.Instance.Observe(Media).Before!.Identity! : new ObservedFileIdentity(123,456,789);
        Row = Db.Recording("candidate", completed: true) with { OutputPath = Media, BundleRegistration = new(null), PublicationReceipt = PublicationReceipt.IssueForTests(Media, 12, identity) };
        Db.Ordinary.Register(Row); Files.Root = Videos; Files.Anchor = Anchor; Files.Api.Anchor = Anchor;
        Native = new(real ? WindowsOriginalFileHoldNative.Instance : Files.Api, real ? Anchor : null);
    }
    internal LocalRetentionApproval Service(RetentionGuardDatabase? database = null, Func<string,bool>? existence = null, ILocalRetentionApprovalUi? ui = null, DeletionLedgerDatabase? ledgerDatabase = null,TimeProvider? clock=null)
        => new(Engine, Db.Store, Db.Ordinary, Sid, Session, Videos, ui ?? Ui, clock ?? Clock, Admission, Native, Files.Admission,
            DatabaseAdmission, ComposedAdmission, database, factsExistence: existence, ledgerDatabase:ledgerDatabase);
    internal async Task<LocalRetentionApproval.Permit> Approve(LocalRetentionApproval a, LocalRetentionApproval.Session s)
    {
        await Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6)); Ui.Decide(LocalRetentionUiChoice.Approve); Ui.Retire.TrySetResult(true);
        var r = await s.Response.WaitAsync(TimeSpan.FromSeconds(6)); Assert.Equal(LocalRetentionState.Approved, r.State); return r.Permit!;
    }
    internal SqliteConnection Writer()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db.Store.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1 }.ToString());
        c.Open(); return c;
    }
    internal void AssertProtected() => OrdinaryPreparationFixture.AssertConflict(Engine, Media);
    internal static async Task<LocalDeletionResult> Finish(LocalRetentionApproval.DeletionOperation op, LocalRetentionApproval.Session s)
    {
        var r = await op.Retirement.WaitAsync(TimeSpan.FromSeconds(8)); Assert.True(r.OurResourcesRetired);
        Assert.True(await s.Retirement.WaitAsync(TimeSpan.FromSeconds(8))); return r;
    }
    internal void AssertNotAddressable()
    {
        // A controlled OPEN_EXISTING result, not File.Exists or free-space inference.
        using var h = WindowsFileMetadataApi.Instance.Open(@"\\?\" + Media, 0x80, 7, 3, 0x00200000, out var error);
        Assert.True(h.IsInvalid); Assert.Equal(2, error); Removed = true;
    }
    public void Dispose()
    {
        try
        {
            Engine.Dispose(); Db.Ordinary.Dispose(); SqliteConnection.ClearAllPools();
            if (!Removed || Replacement)
            {
                Assert.Equal(Bytes, File.ReadAllBytes(Media));
                if (_real && !Replacement) Assert.Equal(Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(Media).Before!.Identity);
            }
            var facts = new { media = Media, database = Db.Store.DatabasePath, original = Anchor, native = _real, Native.Calls,
                requests = Native.Results.ToArray(), Native.DeletePending, original_removed_by_controlled_open = Removed, replacement_preserved = Replacement, preparation = Preparation,
                preserved_original_path = PreservedOriginalPath,
                disposition = Native.Results.Any(r => r.Outcome == OriginalFileDispositionOutcome.Accepted) ? "accepted" : "preserved" };
            File.WriteAllText(Path.Combine(Db.Root, "native-fixture.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }), new UTF8Encoding(false));
        }
        finally { SystemQuery.SetDisplayProvider(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE",_mode); if (_directory is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_directory); }
    }
    internal sealed class Adapter(IOriginalFileHoldNative inner, PublicationIdentityAnchor? realAnchor) : IOriginalFileHoldNative
    {
        internal Action<SafeFileHandle>? BeforeRequest, AfterRequest;
        internal Func<OriginalFileDispositionResult>? FakeRequest;
        internal int Calls;
        internal bool DeletePending;
        internal readonly ConcurrentQueue<OriginalFileDispositionResult> Results = new();
        internal readonly ConcurrentQueue<SafeFileHandle> Handles = new();
        internal SafeFileHandle? Leaf;
        public bool Supported => inner.Supported;
        public uint DriveType(string root) => inner.DriveType(root);
        public SafeFileHandle Open(string path,uint access,uint share,uint creation,uint flags,out int error)
        {
            var h = inner.Open(path,access,share,creation,flags,out error);
            if ((flags & 0x02000000) == 0) { Assert.Equal(0x10080u,access); Assert.Equal(0u,share); Assert.Equal(3u,creation); Assert.Equal(0x00200000u,flags); Leaf=h; }
            return h;
        }
        public bool Basic(SafeFileHandle h,out FileBasicInformation v,out int e)=>inner.Basic(h,out v,out e);
        public bool Standard(SafeFileHandle h,out FileStandardInformation v,out int e)=>inner.Standard(h,out v,out e);
        public bool Identity(SafeFileHandle h,out FileIdentityInformation v,out int e)=>inner.Identity(h,out v,out e);
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int capacity,out int e)=>inner.FinalPath(h,b,capacity,out e);
        public OriginalFileCloseOutcome Close(SafeFileHandle h)=>inner.Close(h);
        public OriginalFileDispositionResult RequestDisposition(SafeFileHandle h)
        {
            Assert.Same(Leaf,h); Interlocked.Increment(ref Calls); Handles.Enqueue(h); BeforeRequest?.Invoke(h);
            if (realAnchor is not null)
            {
                Assert.True(inner.Identity(h,out var identity,out _)); Assert.Equal(realAnchor.Identity,new ObservedFileIdentity(identity.VolumeSerialNumber,identity.IdLow,identity.IdHigh));
                var b=new StringBuilder(2048); Assert.True(inner.FinalPath(h,b,b.Capacity,out _) > 0); Assert.Equal(@"\\?\"+realAnchor.Path,b.ToString());
                Assert.StartsWith(Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task362"))+Path.DirectorySeparatorChar,Path.GetFullPath(realAnchor.Path),StringComparison.OrdinalIgnoreCase);
            }
            var r=FakeRequest?.Invoke() ?? (realAnchor is null ? new(OriginalFileDispositionOutcome.Accepted) : inner.RequestDisposition(h));
            Results.Enqueue(r);
            if (realAnchor is not null && r.Outcome==OriginalFileDispositionOutcome.Accepted)
            { Assert.True(inner.Standard(h,out var standard,out _)); DeletePending=standard.DeletePending!=0; Assert.True(DeletePending); }
            AfterRequest?.Invoke(h);return r;
        }
    }
}
