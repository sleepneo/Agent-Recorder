using AgentRecorder.Core;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class PlanMediaPublicationAnchorTests
{
    [Theory]
    [InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public async Task RetentionFindsValidOtherOwnerPlanPublicationWithSameRunStringAndDifferentPath(string kind)
    {
        using var f = await Fixture.Open(kind); f.Complete(); var anchor = f.Anchor()!;
        using var writer = new SqliteOrdinaryRecordingHistoryStore(f.Store);
        var at = f.Now.AddDays(-2); var path = Path.Combine(f.Root, "Videos", "candidate.mp4");
        var row = new OrdinaryRecordingHistorySnapshot(f.Run, at, "completed", "display", "display_capture", "Synthetic source",
            15, "none", null, null, null, null, path, null, null, null, null, null, null, at.AddSeconds(1), at.AddSeconds(16), "duration_reached", 12, 15)
        { PublicationReceipt = PublicationReceipt.IssueForTests(path, 12, anchor.Identity), BundleRegistration = new(null) };
        writer.Register(row); var files = new RetentionPreviewQueryTests.MetadataFake(); var query = new UnifiedHistoryQueryService(f.Store, writer);
        var service = new RetentionPreviewQueryService(query.GetOrdinaryEvidencePage, f.Root, files, files, () => f.Now.AddDays(2), TestRuntimeOutputUse.None);
        var page = service.GetPage("FOREIGN_CANDIDATE_SID", "FOREIGN_CANDIDATE_SESSION", 100, f.Now.AddDays(1));
        Assert.Equal("shared_publication_identity", Assert.Single(page.Items).ReasonCode);
        Assert.Empty(files.Attributes); Assert.Empty(files.Observations); Assert.Empty(files.Lengths);
        Assert.NotEqual(path, anchor.Path); Assert.NotEqual(12, anchor.Length); Assert.Equal("completed", f.Status().LatestOccurrence!.Status);
    }
}
