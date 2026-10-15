using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Tests;

internal static class PublicationAnchorTestFixture
{
    // Explicit synthetic signing point, never a production default/backfill.
    internal static OrdinaryRecordingHistorySnapshot Anchored(OrdinaryRecordingHistorySnapshot row, bool native = false)
    {
        if (row.Status != "completed" || row.CompletedAtUtc is null || row.OutputSizeBytes is not > 0 ||
            !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(row.OutputPath)) return row;
        var identity = native ? WindowsReadOnlyFileMetadataProbe.Instance.Observe(row.OutputPath).Before!.Identity! : SyntheticObject(row.OutputPath);
        return row with { PublicationReceipt = PublicationReceipt.IssueForTests(row.OutputPath, row.OutputSizeBytes.Value, identity) };
    }
    // A deterministic object model for synthetic tests only: independent paths
    // represent independent objects unless a test explicitly shares a receipt.
    internal static ObservedFileIdentity SyntheticObject(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return new(0x123456789abcdef0, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes) | 1,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)) | 1);
    }
}
