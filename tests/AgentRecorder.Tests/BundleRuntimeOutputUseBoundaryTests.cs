using System.Collections.Concurrent;
using System.Reflection;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private sealed class BundleUseAudit : AuditLogger
    {
        internal Action<string>? Before;
        internal readonly ConcurrentQueue<string> Events = new();
        internal BundleUseAudit() : base(Path.Combine(Path.GetTempPath(), "bundle-use-audit-" + Guid.NewGuid().ToString("N"), "audit.jsonl")) { }
        public override void Log(string name, object value) { Events.Enqueue(name); Before?.Invoke(name); }
    }
    private sealed class BundleUseHistory(SqliteOrdinaryRecordingHistoryStore inner) : IOrdinaryRecordingHistoryStore, IOrdinaryRecordingBundleHistoryWriter
    {
        internal Action? Tail;
        public void Register(OrdinaryRecordingHistorySnapshot s) => inner.Register(s);
        public bool TryPublish(OrdinaryRecordingHistorySnapshot s) => inner.TryPublish(s);
        public OrdinaryRecordingHistoryPage ReadPage(int n, string? c) => inner.ReadPage(n,c);
        public OrdinaryRecordingHistorySnapshot? Read(string id) => inner.Read(id);
        public void RecoverIncomplete(DateTimeOffset t) => inner.RecoverIncomplete(t);
        public bool TryPublishBundle(OrdinaryRecordingBundleUpdate u)
        { var accepted = inner.TryPublishBundle(u); Tail?.Invoke(); return accepted; }
    }
    private static int BundleUseCount(Fixture f)
    {
        lock(OutputLock(f.Engine))
            return ((System.Collections.IEnumerable)typeof(RecordingEngine).GetField("_ordinaryBundleOutputUses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Engine)!).Cast<object>().Count();
    }
    private static Task HostTask(Fixture f)
    {
        Task? host=null;
        Assert.True(SpinWait.SpinUntil(()=>
        {
            lock(OutputLock(f.Engine))
            {
                var uses=(System.Collections.IEnumerable)typeof(RecordingEngine).GetField("_ordinaryBundleOutputUses",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Engine)!;
                var use=Assert.Single(uses.Cast<object>());
                host=(Task?)use.GetType().GetField("Worker",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(use);
                return host is not null;
            }
        },TimeSpan.FromSeconds(5)));
        return host!;
    }
    private static string RuntimeDbFingerprint(Fixture f)
    {
        using var c = f.Store.OpenReadOnlyConnection(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT group_concat(recording_id||':'||status_code||':'||output_path) FROM (SELECT recording_id,status_code,output_path FROM ordinary_recording_history ORDER BY recording_id);";
        var h = cmd.ExecuteScalar()?.ToString();
        cmd.CommandText = "SELECT group_concat(recording_id||':'||status_code||':'||operation_id) FROM (SELECT recording_id,status_code,operation_id FROM ordinary_recording_bundles ORDER BY recording_id);";
        return h + "|" + cmd.ExecuteScalar();
    }
    private static void AssertRuntimeDbProtected(Fixture f, Recording rec, string reason)
    {
        _ = f.Writer.Read(rec.Id); var before = RuntimeDbFingerprint(f);
        var files = new RetentionPreviewQueryTests.MetadataFake(); var query = new UnifiedHistoryQueryService(f.Store, f.Writer, true);
        var service = new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage, f.Root, files, files, () => DateTimeOffset.UtcNow.AddDays(3), f.Engine);
        var page = service.GetPage(Sid, Session, 20, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(reason, Assert.Single(page.Items).ReasonCode);
        Assert.Empty(files.Attributes); Assert.Empty(files.Observations); Assert.Equal(before, RuntimeDbFingerprint(f));
    }


















}
