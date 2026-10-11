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
        var identity = native ? WindowsReadOnlyFileMetadataProbe.Instance.Observe(row.OutputPath).Before!.Identity! : new ObservedFileIdentity(0x123456789abcdef0, 0xfedcba9876543210, 0x876543210abcdef0);
        return row with { PublicationReceipt = PublicationReceipt.IssueForTests(row.OutputPath, row.OutputSizeBytes.Value, identity) };
    }
}
