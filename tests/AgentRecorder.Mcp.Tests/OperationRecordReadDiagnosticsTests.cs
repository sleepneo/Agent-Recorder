using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentRecorder.Mcp.Tests;

public sealed class OperationRecordReadDiagnosticsTests : IDisposable
{
    private readonly string root = Path.Combine(Fixture.Repo, ".local-data", "task434R", "diagnostic-" + Guid.NewGuid().ToString("N"));
    private readonly string id = Guid.NewGuid().ToString("D");
    private OperationStore Store()
    {
        var store = new OperationStore(new BindingStore(Path.Combine(root, "binding.json")));
        Directory.CreateDirectory(store.Root); return store;
    }
    private OperationEntry Entry() => new()
    {
        SchemaVersion = 1, OperationId = id, DurationSeconds = 30, OwnerPid = Environment.ProcessId, OwnerStartedTicks = 1,
        Status = "accepted", ReasonCode = "accepted", Binding = new Binding
        {
            SchemaVersion = 1, PackageRoot = Path.Combine(root, "package"), AppPath = Path.Combine(root, "package", "app.exe"),
            CliPath = Path.Combine(root, "package", "cli.exe"), DataDir = Path.Combine(root, "data"), ProductVersion = "0.1.25",
            InitializationPolicy = "existing_only", InitializationState = "ready", HostEnsureState = "ready",
            DataDirectoryIdentity = "synthetic-directory", DatabaseIdentity = "synthetic-database"
        }
    };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Corrupt(Action action) => Assert.Equal("operation_record_corrupt", Assert.Throws<ContractException>(action).Code);

    [Fact]
    public void OpenHandleReadsOneValidGenerationAcrossProductionAtomicReplace()
    {
        var store = Store(); var initial = Entry(); store.Write(initial);
        var next = initial with { Status = "recording", RecordingId = "rec_123456abcdef", ReasonCode = "mapped" };
        var actual = store.ReadObserved(id, _ => Assert.Fail("Valid publication rejected"), afterOpen: () => store.Write(next));
        Assert.Equal(initial, actual); Assert.Equal(next, store.Read(id));
    }

    [Fact]
    public void InPlaceFixtureTruncationIsDistinctFromAtomicProductionReplace()
    {
        var store = Store(); store.Write(Entry()); OperationStore.ReadEvidence? failure = null;
        Corrupt(() => store.ReadObserved(id, read => failure = read, afterOpen: () => File.WriteAllText(store.PathFor(id), "{")));
        Assert.Equal("json", failure!.Stage); Assert.Equal(new byte[] { (byte)'{' }, failure.Snapshot);
        Assert.True(failure.SnapshotComplete);
    }

    [Fact]
    public void ChecksumFailureRetainsRejectedBytesEvenWhenObserverPublishesNewGeneration()
    {
        var store = Store(); var entry = Entry(); store.Write(entry);
        var path = store.PathFor(id); var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["entry"]!["reason_code"] = "tampered"; File.WriteAllText(path, node.ToJsonString(Json.Options));
        var rejected = File.ReadAllBytes(path); OperationStore.ReadEvidence? failure = null;
        Corrupt(() => store.ReadObserved(id, read => { failure = read; store.Write(entry with { Status = "recording" }); }));
        Assert.Equal("checksum", failure!.Stage); Assert.True(failure.SnapshotComplete);
        Assert.Equal(rejected, failure.Snapshot); Assert.Equal(Hash(rejected), failure.SnapshotSha256);
        Assert.NotEqual(failure.StoredChecksum, failure.ComputedChecksum);
        Assert.Equal("recording", store.Read(id)!.Status); Assert.NotEqual(rejected, File.ReadAllBytes(path));
        var saved = OperationReadFailureEvidence.Save(root, id, failure);
        var firstEvidence = File.ReadAllBytes(saved);
        Assert.Throws<IOException>(() => OperationReadFailureEvidence.Save(root, id, null));
        Assert.Equal(firstEvidence, File.ReadAllBytes(saved));
        File.WriteAllBytes(path, failure.Snapshot!); Corrupt(() => store.Read(id));
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("operation_id")]
    [InlineData("duration_seconds")]
    [InlineData("binding")]
    [InlineData("owner_pid")]
    [InlineData("owner_started_ticks")]
    [InlineData("status")]
    [InlineData("recording_id")]
    [InlineData("confirmation_id")]
    public void ChecksumValidInvalidFieldsReportExactFieldAndStillReject(string field)
    {
        var store = Store(); var entry = Entry();
        entry = field switch
        {
            "schema_version" => entry with { SchemaVersion = 2 }, "duration_seconds" => entry with { DurationSeconds = 31 },
            "binding" => entry with { Binding = null! }, "owner_pid" => entry with { OwnerPid = 0 },
            "owner_started_ticks" => entry with { OwnerStartedTicks = 0 }, "status" => entry with { Status = "bogus" },
            "recording_id" => entry with { RecordingId = "rec_bad" }, "confirmation_id" => entry with { ConfirmationId = "confirm_bad" },
            _ => entry
        };
        store.Write(entry);
        if (field == "operation_id") File.Move(store.PathFor(id), store.PathFor("other"));
        var query = field == "operation_id" ? "other" : id;
        OperationStore.ReadEvidence? failure = null; Corrupt(() => store.ReadObserved(query, read => failure = read));
        Assert.Equal("fields", failure!.Stage); Assert.Equal(field, failure.InvalidField);
        Assert.Equal(failure.StoredChecksum, failure.ComputedChecksum); Corrupt(() => store.Read(query));
    }

    [Theory]
    [InlineData("truncated", "json")]
    [InlineData("duplicate", "json")]
    [InlineData("unknown", "deserialize")]
    [InlineData("missing", "deserialize")]
    [InlineData("utf8", "decode")]
    [InlineData("oversize", "size")]
    public void MalformedSnapshotsRemainFailClosedWithBoundedStageEvidence(string kind, string stage)
    {
        var store = Store(); store.Write(Entry()); var path = store.PathFor(id);
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        byte[] bytes;
        switch (kind)
        {
            case "truncated": bytes = Encoding.UTF8.GetBytes("{"); break;
            case "duplicate": bytes = Encoding.UTF8.GetBytes("{\"entry\":{},\"entry\":{}}"); break;
            case "unknown": node["unexpected"] = true; bytes = Encoding.UTF8.GetBytes(node.ToJsonString()); break;
            case "missing": node["entry"]!.AsObject().Remove("status"); bytes = Encoding.UTF8.GetBytes(node.ToJsonString()); break;
            case "utf8": bytes = [0xff]; break;
            default: bytes = new byte[OperationStore.MaximumRecordBytes + 1]; break;
        }
        File.WriteAllBytes(path, bytes); OperationStore.ReadEvidence? failure = null;
        Corrupt(() => store.ReadObserved(id, read => failure = read)); Assert.Equal(stage, failure!.Stage);
        if (kind == "oversize") Assert.Null(failure.Snapshot);
        else { Assert.Equal(bytes, failure.Snapshot); Assert.Equal(Hash(bytes), failure.SnapshotSha256); }
        Corrupt(() => store.Read(id));
    }

    [Fact]
    public void Utf8BomAndMissingRecordKeepOriginalReaderSemantics()
    {
        var store = Store(); Assert.Null(store.ReadObserved(id, _ => Assert.Fail("Absence is not corruption")));
        var entry = Entry(); store.Write(entry); var path = store.PathFor(id);
        File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, .. File.ReadAllBytes(path)]);
        Assert.Equal(entry, store.Read(id)); Assert.Equal(entry, store.ReadObserved(id, _ => Assert.Fail("BOM rejected")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
