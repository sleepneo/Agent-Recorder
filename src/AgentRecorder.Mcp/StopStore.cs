using System.Security.Cryptography;
using System.Text.Json;

namespace AgentRecorder.Mcp;

// Separate schema: never add fields to OperationEntry's schema-1 checksum input.
internal sealed record StopEntry(int SchemaVersion, string OperationId, string RecordingId,
    Binding Binding, HostStamp Host, string Status, string ReasonCode, string? ProductStatus = null, string? ProductErrorCode = null);
internal sealed record StopResult(bool Ok, string Status, string ReasonCode, string OperationId, string? RecordingId,
    string? ProductStatus = null, string? ProductErrorCode = null) : IToolResult;

internal sealed class StopStore(OperationStore operations)
{
    private sealed record Envelope(StopEntry Entry, string Checksum);
    public string Root => LocalPaths.Normalize(Path.Combine(operations.Root, "stops"));
    public string PathFor(string id) => LocalPaths.Normalize(Path.Combine(Root, id + ".json"));
    private string Marker(string id) => LocalPaths.Normalize(Path.Combine(Root, id + ".intent"));
    public bool HasEvidence(string id) => File.Exists(Marker(id));
    public StopEntry? Read(string id)
    {
        if (File.Exists(Root)) throw new ContractException("stop_evidence_corrupt");
        var path = PathFor(id);
        if (Directory.Exists(path)) throw new ContractException("stop_evidence_corrupt");
        if (!File.Exists(path))
        {
            if (HasEvidence(id)) throw new ContractException("stop_evidence_missing");
            return null;
        }
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > OperationStore.MaximumRecordBytes) throw new ContractException("stop_evidence_corrupt");
            using var reader = new StreamReader(file, new System.Text.UTF8Encoding(false, true));
            using var json = Json.Read(reader.ReadToEnd());
            var envelope = json.RootElement.Deserialize<Envelope>(Json.Options)!;
            var entry = envelope.Entry;
            if (entry == null || envelope.Checksum != Checksum(entry) || entry.SchemaVersion != 1 || entry.OperationId != id ||
                !OperationManager.OperationIdValid(id) || !OperationManager.NativeId(entry.RecordingId, "rec_") ||
                entry.Binding == null || entry.Host == null || entry.Status is not ("intent" or "accepted" or "rejected"))
                throw new ContractException("stop_evidence_corrupt");
            return entry;
        }
        catch (ContractException) { throw; }
        catch { throw new ContractException("stop_evidence_corrupt"); }
    }

    // Both methods require the existing cross-process operation gate. No HTTP
    // occurs while that gate is held. Markers survive incomplete intent writes.
    public void WriteIntent(StopEntry entry)
    {
        Directory.CreateDirectory(Root);
        if (Directory.EnumerateFiles(Root, "*.intent").Take(OperationStore.MaximumRecords + 1).Count() >= OperationStore.MaximumRecords ||
            Directory.EnumerateFiles(Root, "*.json").Take(OperationStore.MaximumRecords + 1).Count() >= OperationStore.MaximumRecords)
            throw new ContractException("stop_capacity_exceeded");
        using (var marker = new FileStream(Marker(entry.OperationId), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { marker.WriteByte(1); marker.Flush(true); }
        Write(entry);
    }

    public void Write(StopEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(entry, Checksum(entry)), Json.Options);
        if (bytes.Length > OperationStore.MaximumRecordBytes) throw new ContractException("stop_evidence_corrupt");
        var path = PathFor(entry.OperationId);
        var temp = LocalPaths.Normalize(Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Checksum(StopEntry entry) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, Json.Options)));
}
