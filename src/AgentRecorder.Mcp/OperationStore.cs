using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

namespace AgentRecorder.Mcp;

internal sealed record HostStamp(int Pid, int Port, string InstanceId, long StartedTicks, string Executable, string UserSid);

internal sealed record OperationEntry
{
    public required int SchemaVersion { get; init; }
    public required string OperationId { get; init; }
    public required int DurationSeconds { get; init; }
    public required Binding Binding { get; init; }
    public required int OwnerPid { get; init; }
    public required long OwnerStartedTicks { get; init; }
    public required string Status { get; init; }
    public required string ReasonCode { get; init; }
    public HostStamp? Host { get; init; }
    public string? RecordingId { get; init; }
    public string? ConfirmationId { get; init; }
    public string? ProductStatus { get; init; }
    public string? ProductErrorCode { get; init; }
    public bool Terminal { get; init; }
}

internal sealed record OperationResult(bool Ok, string Status, string ReasonCode, string OperationId,
    string? RecordingId = null, string? ConfirmationId = null, string? ProductStatus = null,
    string? ProductErrorCode = null, string? OutputPath = null, long? OutputSizeBytes = null,
    double? OutputDurationSeconds = null) : IToolResult;

internal sealed class OperationStore(BindingStore binding)
{
    internal sealed record ReadEvidence(string Stage, string? InvalidField, byte[]? Snapshot, bool SnapshotComplete,
        string? SnapshotSha256, string? StoredChecksum, string? ComputedChecksum, string ExceptionType, int HResult);
    private sealed record Envelope(OperationEntry Entry, string Checksum);
    public const int MaximumRecords = 128;
    public const int MaximumRecordBytes = 16384;
    public string Root { get; } = LocalPaths.Normalize(binding.Filename + ".operations");
    internal Action? BeforePublicationRecheck { get; init; }
    internal Action? BeforePublish { get; init; }

    public void ValidateLocation(Binding value)
    {
        binding.ValidateLocation(value.PackageRoot, value.DataDir);
        if (LocalPaths.Contains(value.PackageRoot, Root) || LocalPaths.Contains(value.DataDir, Root))
            throw new ContractException("binding_location_conflict");
        LocalPaths.Normalize(Root);
    }

    public string PathFor(string id) => LocalPaths.Normalize(Path.Combine(Root, id + ".json"));
    public bool HasEvidence(string id) => File.Exists(LocalPaths.Normalize(Path.Combine(Root, id + ".worker")));

    public OperationEntry? Read(string id) => ReadObserved(id, null);

    // The observer is internal and supplied only by synthetic tests, never an MCP
    // argument/environment switch. It sees this read's bounded bytes, not a reopen.
    internal OperationEntry? ReadObserved(string id, Action<ReadEvidence>? observer, Action? afterOpen = null, Action? beforeOpen = null)
        => ReadCore(id, observer, afterOpen, beforeOpen, gateHeld: false, knownRecord: false);

    // Only callers already holding LockAsync may use this path. Never reacquire
    // the non-reentrant gate from Save/Admission/Stop's locked regions.
    internal OperationEntry? ReadUnderGate(string id) => ReadCore(id, null, null, null, gateHeld: true, knownRecord: false);

    private OperationEntry? ReadCore(string id, Action<ReadEvidence>? observer, Action? afterOpen, Action? beforeOpen,
        bool gateHeld, bool knownRecord)
    {
        var stage = "root"; ReadCaptureStream? capture = null; string? invalid = null, stored = null, computed = null;
        bool delegated = false;
        try
        {
            if (File.Exists(LocalPaths.Normalize(Root))) throw new ContractException("operation_record_corrupt");
            var path = PathFor(id);
            stage = "existence";
            if (!File.Exists(path))
            {
                if (!knownRecord && !HasEvidence(id)) return null;
                if (gateHeld) throw new ContractException(HasEvidence(id) ? "operation_record_missing" : "operation_record_unavailable");
                delegated = true;
                return ReadAfterPublication(id, observer, afterOpen, beforeOpen, knownRecord: true);
            }
            stage = "open";
            beforeOpen?.Invoke();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            afterOpen?.Invoke();
            stage = "size";
            if (file.Length > MaximumRecordBytes) throw new ContractException("operation_record_corrupt");
            // Keep the ordinary StreamReader path and sharing flags. The test-only
            // wrapper copies bytes as that same reader consumes them; no reopen,
            // retry, or change to the production read schedule/buffer size.
            if (observer != null) capture = new ReadCaptureStream(file);
            stage = "read_decode";
            using var reader = new StreamReader((Stream?)capture ?? file, new System.Text.UTF8Encoding(false, true));
            var text = reader.ReadToEnd();
            stage = "json";
            using var json = Json.Read(text);
            stage = "deserialize";
            var envelope = json.RootElement.Deserialize<Envelope>(Json.Options)!;
            var entry = envelope.Entry;
            stage = "checksum"; stored = envelope.Checksum;
            computed = entry == null ? null : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, Json.Options)));
            if (entry == null || stored != computed)
                throw new ContractException("operation_record_corrupt");
            stage = "fields"; invalid = InvalidField(entry, id);
            if (invalid != null) throw new ContractException("operation_record_corrupt");
            return entry;
        }
        catch (Exception error)
        {
            if (delegated) throw; // The coordinated read owns its exact evidence.
            if (!gateHeld && stage == "open" && error is FileNotFoundException or DirectoryNotFoundException)
                return ReadAfterPublication(id, observer, afterOpen, beforeOpen, knownRecord: true);
            if (stage == "read_decode") stage = error is System.Text.DecoderFallbackException ? "decode" : "read";
            var snapshot = capture?.Snapshot;
            observer?.Invoke(new(stage, invalid, snapshot, capture?.Complete == true,
                snapshot == null ? null : Convert.ToHexString(SHA256.HashData(snapshot)), stored, computed, error.GetType().Name, error.HResult));
            if (error is ContractException) throw;
            if (error is FileNotFoundException or DirectoryNotFoundException)
                throw new ContractException("operation_record_unavailable");
            if (error is IOException or UnauthorizedAccessException) throw new ContractException("operation_record_unavailable");
            throw new ContractException("operation_record_corrupt");
        }
    }

    private OperationEntry? ReadAfterPublication(string id, Action<ReadEvidence>? observer, Action? afterOpen,
        Action? beforeOpen, bool knownRecord)
    {
        // Exactly one coordinated reacquisition, same UUID, no HTTP and no write.
        // Never recreate a missing journal directory just to observe it.
        if (!Directory.Exists(Root)) throw new ContractException("operation_record_unavailable");
        BeforePublicationRecheck?.Invoke();
        FileStream gate;
        try { gate = LockCoreAsync(CancellationToken.None, createDirectory: false).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            observer?.Invoke(new("coordination", null, null, false, null, null, null, error.GetType().Name, error.HResult));
            if (error is ContractException contract && contract.Code != "operation_store_busy") throw;
            throw new ContractException("operation_record_unavailable");
        }
        using (gate) return ReadCore(id, observer, afterOpen, beforeOpen, gateHeld: true, knownRecord);
    }

    private sealed class ReadCaptureStream(Stream source) : Stream
    {
        private readonly MemoryStream bytes = new();
        private long total;
        private bool reachedEnd;
        public byte[] Snapshot => bytes.ToArray();
        public bool Complete => reachedEnd && total <= MaximumRecordBytes + 1;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read == 0) reachedEnd = true;
            var copy = Math.Min(read, MaximumRecordBytes + 1 - (int)bytes.Length);
            if (copy > 0) bytes.Write(buffer, offset, copy);
            total += read;
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string? InvalidField(OperationEntry entry, string id) => entry.SchemaVersion != 1 ? "schema_version" :
        entry.OperationId != id ? "operation_id" : entry.DurationSeconds is < 1 or > 30 ? "duration_seconds" :
        entry.Binding is null ? "binding" : entry.OwnerPid <= 0 ? "owner_pid" : entry.OwnerStartedTicks <= 0 ? "owner_started_ticks" :
        entry.Status is not ("accepted" or "submitting" or "waiting_for_local_confirmation" or "preparing" or "countdown" or
            "recording" or "stopping" or "finalizing" or "completed" or "failed" or "cancelled" or "rejected" or "expired" or "unknown" or "not_submitted" or "no_recording") ? "status" :
        entry.RecordingId != null && !OperationManager.NativeId(entry.RecordingId, "rec_") ? "recording_id" :
        entry.ConfirmationId != null && !OperationManager.NativeId(entry.ConfirmationId, "confirm_") ? "confirmation_id" : null;

    public IEnumerable<OperationEntry> Entries() => EntriesCore(gateHeld: false);
    internal IEnumerable<OperationEntry> EntriesUnderGate() => EntriesCore(gateHeld: true);
    private IEnumerable<OperationEntry> EntriesCore(bool gateHeld)
    {
        if (!Directory.Exists(Root)) yield break;
        var admissions = Directory.EnumerateFiles(Root, "*.worker").Take(MaximumRecords + 1).ToArray();
        if (admissions.Length > MaximumRecords) throw new ContractException("operation_capacity_exceeded");
        foreach (var admission in admissions)
        {
            var id = Path.GetFileNameWithoutExtension(admission);
            if (!OperationManager.OperationIdValid(id)) throw new ContractException("operation_record_missing");
            if (!File.Exists(PathFor(id)) && (gateHeld ? ReadUnderGate(id) : Read(id)) == null)
                throw new ContractException("operation_record_missing");
        }
        var paths = Directory.EnumerateFiles(Root, "*.json").Take(MaximumRecords + 1).ToArray();
        if (paths.Length > MaximumRecords) throw new ContractException("operation_capacity_exceeded");
        foreach (var path in paths)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!OperationManager.OperationIdValid(id)) throw new ContractException("operation_record_corrupt");
            yield return (gateHeld ? ReadUnderGate(id) : Read(id)) ?? throw new ContractException("operation_record_missing");
        }
    }

    public Task<FileStream> LockAsync(CancellationToken token) => LockCoreAsync(token, createDirectory: true);
    private async Task<FileStream> LockCoreAsync(CancellationToken token, bool createDirectory)
    {
        token.ThrowIfCancellationRequested();
        LocalPaths.Normalize(Root);
        if (createDirectory) Directory.CreateDirectory(Root);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(LocalPaths.Normalize(Path.Combine(Root, ".gate")), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.ElapsedMilliseconds < 1500) { await Task.Delay(30, token); }
            catch (IOException) { throw new ContractException("operation_store_busy"); }
        }
    }

    public FileStream WorkerLease(string id)
    {
        var file = new FileStream(LocalPaths.Normalize(Path.Combine(Root, id + ".worker")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try { file.WriteByte(1); file.Flush(true); return file; }
        catch { file.Dispose(); throw; }
    }

    public bool WorkerAlive(OperationEntry entry)
    {
        try
        {
            using var owner = Process.GetProcessById(entry.OwnerPid);
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != entry.OwnerStartedTicks) return false;
            try { using var probe = new FileStream(LocalPaths.Normalize(Path.Combine(Root, entry.OperationId + ".worker")), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (FileNotFoundException) { return false; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return true; }
            return false;
        }
        catch { return false; }
    }

    public void Write(OperationEntry entry)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, Json.Options)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(entry, checksum), Json.Options);
        if (bytes.Length > MaximumRecordBytes) throw new ContractException("operation_record_corrupt");
        var path = PathFor(entry.OperationId);
        var temp = LocalPaths.Normalize(Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp"));
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            BeforePublish?.Invoke();
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static bool SameBinding(Binding left, Binding right) =>
        LocalPaths.Equal(left.PackageRoot, right.PackageRoot) && LocalPaths.Equal(left.AppPath, right.AppPath) &&
        LocalPaths.Equal(left.CliPath, right.CliPath) && LocalPaths.Equal(left.DataDir, right.DataDir) &&
        left.ProductVersion == right.ProductVersion && left.DataDirectoryIdentity == right.DataDirectoryIdentity &&
        (left.DatabaseIdentity == null || left.DatabaseIdentity == right.DatabaseIdentity);
}
