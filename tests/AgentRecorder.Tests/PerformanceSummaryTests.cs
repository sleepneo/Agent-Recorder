using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Headless;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

public sealed class PerformanceSummaryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _perfDir;
    private readonly ITestOutputHelper _output;

    public PerformanceSummaryTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRecorder.Tests.PerfSummary", Guid.NewGuid().ToString("N"));
        _perfDir = Path.Combine(_tempDir, "perf");
        Directory.CreateDirectory(_perfDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingFiles_ReturnsNoData_WithColdWarmStructure()
    {
        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("no_data", summary.Status);
        Assert.Equal(1, summary.SchemaVersion);
        Assert.True(summary.Groups.ContainsKey("cold"));
        Assert.True(summary.Groups.ContainsKey("warm"));
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.Equal(0, summary.Groups["warm"].TraceCount);
        Assert.Equal(50, summary.Window.MaxTracesPerGroup);
        Assert.Equal("local_rolling_jsonl", summary.Window.Source);
    }

    [Fact]
    public void EmptyFile_ReturnsNoData()
    {
        File.WriteAllText(BasePath(), "");
        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("no_data", summary.Status);
    }

    [Fact]
    public void SingleColdTrace_GroupsAndCalculatesSixMetrics()
    {
        WriteTrace("t1", "cold", new (string Event, double Elapsed)[]
        {
            ("intent.accepted", 0),
            ("confirmation.shown", 100),
            ("confirmation.approved", 250),
            ("capture.first_frame_observed", 700)
        }, ensureMs: 730, serviceStartupMs: 1200);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("available", summary.Status);
        var cold = summary.Groups["cold"];
        Assert.Equal(1, cold.TraceCount);
        Assert.Equal("preliminary", cold.Quality);

        AssertMetric(cold, "ensure_running_ms", sampleCount: 1, p50: 730.0, p95: 730.0);
        AssertMetric(cold, "service_startup_ms", sampleCount: 1, p50: 1200.0, p95: 1200.0);
        AssertMetric(cold, "request_to_confirmation_shown_ms", sampleCount: 1, p50: 100.0, p95: 100.0);
        AssertMetric(cold, "confirmation_shown_to_approved_ms", sampleCount: 1, p50: 150.0, p95: 150.0);
        AssertMetric(cold, "approved_to_first_frame_progress_ms", sampleCount: 1, p50: 450.0, p95: 450.0);
        AssertMetric(cold, "request_to_first_frame_progress_ms", sampleCount: 1, p50: 700.0, p95: 700.0);
    }

    [Theory]
    [InlineData(1, 100.0, 100.0, "preliminary")]
    [InlineData(2, 100.0, 200.0, "preliminary")]
    [InlineData(19, 1000.0, 1900.0, "preliminary")]
    [InlineData(20, 1000.0, 1900.0, "representative")]
    public void PercentileNearestRank_Boundaries(int count, double expectedP50, double expectedP95, string expectedQuality)
    {
        var values = Enumerable.Range(1, count).Select(i => i * 100.0).ToArray();
        WriteColdTraces(values);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        var cold = summary.Groups["cold"];
        Assert.Equal(expectedQuality, cold.Quality);
        AssertMetric(cold, "request_to_first_frame_progress_ms", sampleCount: count, p50: expectedP50, p95: expectedP95);
    }

    [Fact]
    public void ColdAndWarm_AreSeparated()
    {
        WriteTrace("tc1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 800.0) }, ensureMs: 700);
        WriteTrace("tc2", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 900.0) }, ensureMs: 800);
        WriteTrace("tw1", "warm", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 50);
        WriteTrace("tw2", "warm", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 300.0) }, ensureMs: 60);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal(2, summary.Groups["cold"].TraceCount);
        Assert.Equal(2, summary.Groups["warm"].TraceCount);
        AssertMetric(summary.Groups["cold"], "ensure_running_ms", sampleCount: 2, p50: 700.0, p95: 800.0);
        AssertMetric(summary.Groups["warm"], "ensure_running_ms", sampleCount: 2, p50: 50.0, p95: 60.0);
    }

    [Fact]
    public void UnclassifiedTraceCount_IncludesMissingInvalidAndReused()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0) }, ensureMs: 100, status: "consumed"); // valid
        WriteTrace("t2", "cold", new[] { ("intent.accepted", 0.0) }, ensureMs: 100, status: "reused"); // invalid status
        WriteTrace("t3", "warm", new[] { ("intent.accepted", 0.0) }, ensureMs: -1, status: "consumed"); // negative ensure
        WriteTrace("t4", null, new[] { ("intent.accepted", 0.0) }, ensureMs: 100, status: "consumed"); // no kind
        WriteTrace("t5", "unknown", new[] { ("intent.accepted", 0.0) }, ensureMs: 100, status: "consumed"); // unknown kind

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(1, summary.Groups["cold"].TraceCount);
        Assert.Equal(0, summary.Groups["warm"].TraceCount);
        Assert.Equal(4, summary.Quality.UnclassifiedTraceCount);
        Assert.True(summary.Quality.DiscardedSampleCount >= 1, "Invalid context should be counted as discarded");
    }

    [Fact]
    public void MissingEvents_ReduceSampleCountPerMetric()
    {
        // Only intent.accepted + confirmation.shown + first_frame: approved metrics drop.
        WriteTrace("t1", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("confirmation.shown", 100.0),
            ("capture.first_frame_observed", 400.0)
        }, ensureMs: 200);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        var cold = summary.Groups["cold"];
        AssertMetric(cold, "ensure_running_ms", sampleCount: 1);
        AssertMetric(cold, "request_to_confirmation_shown_ms", sampleCount: 1);
        Assert.False(cold.Metrics.ContainsKey("confirmation_shown_to_approved_ms"));
        Assert.False(cold.Metrics.ContainsKey("approved_to_first_frame_progress_ms"));
        AssertMetric(cold, "request_to_first_frame_progress_ms", sampleCount: 1);
    }

    [Fact]
    public void RejectedExpiredAndCaptureFailed_DoNotProduceApprovedToFirstFrame()
    {
        WriteTrace("t1", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("confirmation.shown", 100.0),
            ("confirmation.rejected", 150.0)
        }, ensureMs: 100);

        WriteTrace("t2", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("confirmation.shown", 100.0),
            ("confirmation.expired", 200.0)
        }, ensureMs: 100);

        WriteTrace("t3", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("confirmation.shown", 100.0),
            ("confirmation.approved", 250.0),
            ("capture.backend_start_failed", 260.0)
        }, ensureMs: 100);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        var cold = summary.Groups["cold"];
        Assert.False(cold.Metrics.ContainsKey("approved_to_first_frame_progress_ms"));
        Assert.False(cold.Metrics.ContainsKey("request_to_first_frame_progress_ms"));
    }

    [Fact]
    public void ClientHints_DoNotAffectServerPercentiles()
    {
        var lines = new List<string>();
        lines.Add(MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, serviceStartupMs: 200,
            status: "consumed",
            clientHints: new Dictionary<string, object?> { ["agent_to_server_hint_ms"] = 99999.0 }));
        lines.Add(MakeEvent("t1", "capture.first_frame_observed", 300));
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        AssertMetric(summary.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 1, p50: 300.0);
    }

    [Fact]
    public void MalformedTruncatedAndUnknown_IsolatedAndCounted()
    {
        var lines = new List<string>
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: "consumed"),
            MakeEvent("t1", "capture.first_frame_observed", 200),
            "this is not json",
            "{\"schema_version\":1,\"trace_id\":\"t2\",\"event\":\"intent.accepted\",\"timestamp_utc\":\"2026-07-18T00:00:00Z\",\"elapsed_from_intent_ms\":0,\"startup_kind\":\"cold\",\"ensure_elapsed_ms\":100,\"ensure_context_status\":\"consumed\"", // truncated
            "{\"schema_version\":2,\"trace_id\":\"t3\",\"event\":\"intent.accepted\",\"timestamp_utc\":\"2026-07-18T00:00:00Z\",\"elapsed_from_intent_ms\":0}",
            MakeEvent("t4", "weird.unknown_event", 0, startupKind: "cold", ensureMs: 100, status: "consumed"),
            MakeEvent("t4", "capture.first_frame_observed", 50) // unknown event ignored, but first_frame still paired
        };
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("partial_data", summary.Quality.ReasonCode);
        Assert.Equal(2, summary.Quality.MalformedLineCount); // bad json + truncated
        Assert.Equal(1, summary.Quality.UnsupportedSchemaCount);
        Assert.Equal(2, summary.Groups["cold"].TraceCount);
    }

    [Fact]
    public void NegativeAndInverseLatencies_AreDiscarded()
    {
        WriteTrace("t1", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("confirmation.shown", 100.0),
            ("confirmation.approved", 50.0) // inverse: approved before shown
        }, ensureMs: 100);

        WriteTrace("t2", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("confirmation.shown", 100.0),
            ("confirmation.approved", -10.0) // negative
        }, ensureMs: 100);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal(2, summary.Quality.DiscardedSampleCount);
        Assert.False(summary.Groups["cold"].Metrics.ContainsKey("confirmation_shown_to_approved_ms"));
    }

    [Fact]
    public void DuplicateEvents_KeepEarliestOccurrence()
    {
        var lines = new List<string>
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: "consumed"),
            MakeEvent("t1", "confirmation.shown", 100),
            MakeEvent("t1", "confirmation.shown", 999), // duplicate ignored
            MakeEvent("t1", "confirmation.approved", 150),
            MakeEvent("t1", "capture.first_frame_observed", 200)
        };
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        AssertMetric(summary.Groups["cold"], "confirmation_shown_to_approved_ms", sampleCount: 1, p50: 50.0);
    }

    [Fact]
    public void DuplicateEvents_CrossFile_SelectsEarliestIgnoringEnumerationOrder()
    {
        // Newer base file contains a *later* value; older .1 history contains
        // the earliest value. Provider reads base first, then .1.
        var history = HistoryPath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.WriteAllLines(history, new[]
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: "consumed", timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc)),
            MakeEvent("t1", "confirmation.shown", 50, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(50)),
            MakeEvent("t1", "confirmation.approved", 150, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(150)),
            MakeEvent("t1", "capture.first_frame_observed", 200, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(200))
        });

        File.WriteAllLines(BasePath(), new[]
        {
            MakeEvent("t1", "confirmation.shown", 999, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 1, DateTimeKind.Utc))
        });

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        AssertMetric(summary.Groups["cold"], "confirmation_shown_to_approved_ms", sampleCount: 1, p50: 100.0);
        AssertMetric(summary.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 1, p50: 200.0);
    }

    [Fact]
    public void DuplicateEvents_InvalidThenValid_ReplacesWithValid()
    {
        var lines = new List<string>
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: "consumed"),
            MakeEvent("t1", "confirmation.shown", -10.0),
            MakeEvent("t1", "confirmation.shown", 100.0),
            MakeEvent("t1", "confirmation.approved", 150.0),
            MakeEvent("t1", "capture.first_frame_observed", 200.0)
        };
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        AssertMetric(summary.Groups["cold"], "confirmation_shown_to_approved_ms", sampleCount: 1, p50: 50.0);
    }

    [Fact]
    public void DuplicateEvents_TieBreaker_UsesEarlierTimestamp()
    {
        var baseTime = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: "consumed", timestampUtc: baseTime),
            MakeEvent("t1", "confirmation.shown", 100, timestampUtc: baseTime.AddMilliseconds(100)),
            MakeEvent("t1", "confirmation.shown", 100, timestampUtc: baseTime.AddMilliseconds(50)), // same elapsed, earlier timestamp
            MakeEvent("t1", "confirmation.approved", 150, timestampUtc: baseTime.AddMilliseconds(150)),
            MakeEvent("t1", "capture.first_frame_observed", 200, timestampUtc: baseTime.AddMilliseconds(200))
        };
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        // confirmation.shown chosen is the one with elapsed=100 and earlier timestamp,
        // so shown->approved = 150 - 100 = 50.
        AssertMetric(summary.Groups["cold"], "confirmation_shown_to_approved_ms", sampleCount: 1, p50: 50.0);
    }

    [Fact]
    public void CrossFileMerge_HistoryAndCurrentCombined()
    {
        // Current file
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);
        // Roll manually to history file .1
        var history = HistoryPath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.Move(BasePath(), history);
        // New current file
        WriteTrace("t2", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 20);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal(2, summary.Groups["cold"].TraceCount);
        AssertMetric(summary.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 2, p50: 100.0, p95: 200.0);
    }

    [Fact]
    public void MostRecentNTraces_WhenExceeded_KeepsNewest()
    {
        // Write 5 cold traces with increasing first-frame latency.
        for (int i = 0; i < 5; i++)
        {
            var ts = new DateTime(2026, 7, 18, 0, 0, i, DateTimeKind.Utc);
            WriteTrace($"t{i}", "cold",
                new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", (i + 1) * 100.0) },
                ensureMs: 10, timestamp: ts);
        }

        var provider = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTracesPerGroup: 2);
        var summary = provider.GetSummary();

        Assert.Equal(2, summary.Groups["cold"].TraceCount);
        // Newest are t3 (400) and t4 (500). Nearest-rank P50 = rank 1 -> 400.
        AssertMetric(summary.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 2, p50: 400.0, p95: 500.0);
    }

    [Fact]
    public void Cache_TtlPreventsRescan()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);

        var now = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
        var provider = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), cacheTtl: TimeSpan.FromSeconds(5), utcNow: () => now);

        var first = provider.GetSummary();
        Assert.Equal(1, first.Groups["cold"].TraceCount);

        // Add a new trace; because of TTL it should not appear yet.
        WriteTrace("t2", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 10);
        var second = provider.GetSummary();
        Assert.Equal(1, second.Groups["cold"].TraceCount);

        // Advance time beyond TTL; new trace should appear.
        now += TimeSpan.FromSeconds(6);
        var third = provider.GetSummary();
        Assert.Equal(2, third.Groups["cold"].TraceCount);
    }

    [Fact]
    public void Cache_BoundaryReachedRetainsPartialStats()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            cacheTtl: TimeSpan.Zero,
            maxBytesPerFile: 400);

        var first = provider.GetSummary();
        Assert.Equal("available", first.Status);
        Assert.Equal(1, first.Groups["cold"].TraceCount);

        // Make the history file exceed the per-file byte boundary so the next
        // refresh hits a boundary. The previously cached base-file trace must
        // remain.
        var line = "{\"schema_version\":1,\"trace_id\":\"x\",\"event\":\"intent.accepted\",\"timestamp_utc\":\"2026-07-18T00:00:00Z\",\"elapsed_from_intent_ms\":0}";
        var huge = string.Join("\n", Enumerable.Range(0, 100).Select(_ => line));
        File.WriteAllText(HistoryPath(1), huge);

        var second = provider.GetSummary();
        Assert.Equal("degraded", second.Status);
        Assert.Equal(1, second.Groups["cold"].TraceCount);
        Assert.Equal("read_boundary_reached", second.Quality.ReasonCode);
    }

    [Fact]
    public void ReadFailure_ReturnsDegradedWithoutThrowing()
    {
        // Point the provider at a directory path so FileStream fails.
        var provider = new RollingJsonlPerformanceSummaryProvider(basePath: _perfDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_error", summary.Quality.ReasonCode);
        Assert.True(summary.Groups.ContainsKey("cold"));
        Assert.True(summary.Groups.ContainsKey("warm"));
    }

    [Fact]
    public void FileShare_AllowsWriterToRenameWhileReading()
    {
        File.WriteAllText(BasePath(), "hello world\n");

        using var stream = RollingJsonlPerformanceSummaryProvider.OpenFileWithSharedDelete(BasePath());
        var renamed = BasePath() + ".renamed";
        File.Move(BasePath(), renamed, overwrite: true);
        Assert.True(File.Exists(renamed), "File should have been renamed while the read handle was open");

        // The provider can still consume the open stream; restoring the file
        // keeps the temp directory clean.
        stream.Dispose();
        File.Move(renamed, BasePath(), overwrite: true);
    }

    [Fact]
    public void RollingConcurrency_WriterRollsWhileProviderReads()
    {
        var writer = new RollingJsonlWriter(BasePath(), maxFileSize: 100, maxHistoryFiles: 3);
        writer.WriteLineSynchronously(MakeEvent("t0", "intent.accepted", 0, startupKind: "cold", ensureMs: 10, status: "consumed"));

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);

        using var heldStream = RollingJsonlPerformanceSummaryProvider.OpenFileWithSharedDelete(BasePath());

        // Force a roll while the provider has an open read handle.
        var rolled = new List<Exception?>();
        Parallel.Invoke(
            () =>
            {
                try
                {
                    writer.WriteLineSynchronously(MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 10, status: "consumed"));
                    writer.WriteLineSynchronously(MakeEvent("t2", "intent.accepted", 0, startupKind: "cold", ensureMs: 10, status: "consumed"));
                }
                catch (Exception ex)
                {
                    rolled.Add(ex);
                }
            },
            () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try { provider.GetSummary(); }
                    catch (Exception ex) { rolled.Add(ex); }
                }
            }
        );

        heldStream.Dispose();
        writer.Dispose();

        Assert.Empty(rolled);
        var summary = provider.GetSummary();
        Assert.NotNull(summary);
        Assert.True(summary.Groups.ContainsKey("cold"));
        Assert.True(summary.Groups.ContainsKey("warm"));
    }

    [Fact]
    public void SingleFileExceedsMaxBytes_ReturnsDegraded()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);
        WriteTrace("t2", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 10);

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxBytesPerFile: 300);

        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
        Assert.True(summary.Groups["cold"].TraceCount >= 1, "Valid traces processed before the boundary should be retained");
    }

    [Fact]
    public void TotalBytesExceedsMax_ReturnsDegradedAcrossFiles()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);
        var history = HistoryPath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.Move(BasePath(), history);

        WriteTrace("t2", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 10);

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxBytesPerFile: 5L * 1024 * 1024,
            maxTotalBytes: 300);

        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
        Assert.True(summary.Groups["cold"].TraceCount >= 1);
    }

    [Fact]
    public void Utf8ByteCounting_UsesBytesNotStringLength()
    {
        // 100 'é' characters = 100 chars but 200 UTF-8 bytes.
        var padding = new string('é', 100);
        var line = "{\"schema_version\":1,\"trace_id\":\"t1\",\"event\":\"intent.accepted\",\"timestamp_utc\":\"2026-07-18T00:00:00Z\",\"elapsed_from_intent_ms\":0,\"startup_kind\":\"cold\",\"ensure_elapsed_ms\":10,\"ensure_context_status\":\"consumed\",\"pad\":\"" + padding + "\"}";
        File.WriteAllText(BasePath(), line + "\n");

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxTotalBytes: 250); // bytes exceed this, chars do not

        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
    }

    [Fact]
    public void LongLine_BoundedAndDegraded()
    {
        var longValue = new string('a', 500);
        var line = "{\"schema_version\":1,\"trace_id\":\"t1\",\"event\":\"intent.accepted\",\"timestamp_utc\":\"2026-07-18T00:00:00Z\",\"elapsed_from_intent_ms\":0,\"pad\":\"" + longValue + "\"}";
        File.WriteAllText(BasePath(), line + "\n");

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxLineBytes: 50);

        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
        Assert.Equal(0, summary.Quality.MalformedLineCount);
    }

    [Fact]
    public void LongLine_DoesNotProcessSubsequentLines()
    {
        // A long line must trigger read_boundary_reached and stop scanning,
        // so the valid trace after it is not processed as partial data.
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);

        var hugePad = new string('a', 5000);
        var longLine = "{\"schema_version\":1,\"trace_id\":\"t2\",\"event\":\"intent.accepted\",\"timestamp_utc\":\"2026-07-18T00:00:00Z\",\"elapsed_from_intent_ms\":0,\"startup_kind\":\"cold\",\"ensure_elapsed_ms\":10,\"ensure_context_status\":\"consumed\",\"pad\":\"" + hugePad + "\"}";
        File.AppendAllText(BasePath(), longLine + "\n");

        WriteTrace("t3", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 10);

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxLineBytes: 300);

        var summary = provider.GetSummary();

        Assert.True(summary.Status == "degraded" && summary.Quality.ReasonCode == "read_boundary_reached" && summary.Groups["cold"].TraceCount == 1,
            $"Status={summary.Status}, Reason={summary.Quality.ReasonCode}, Traces={summary.Groups["cold"].TraceCount}, Malformed={summary.Quality.MalformedLineCount}, Discarded={summary.Quality.DiscardedSampleCount}, Unclassified={summary.Quality.UnclassifiedTraceCount}");
        AssertMetric(summary.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 1, p50: 100.0);
    }

    [Fact]
    public void StaleCache_OnReadError_ReturnsCachedData()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);

        var shouldFail = false;
        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            openFile: path => shouldFail
                ? throw new IOException("simulated read error")
                : RollingJsonlPerformanceSummaryProvider.OpenFileWithSharedDelete(path),
            cacheTtl: TimeSpan.Zero);

        var initial = provider.GetSummary();
        Assert.Equal("available", initial.Status);
        Assert.Equal(1, initial.Groups["cold"].TraceCount);

        shouldFail = true;
        var stale = provider.GetSummary();

        Assert.Equal("degraded", stale.Status);
        Assert.Equal("stale_snapshot", stale.Quality.ReasonCode);
        Assert.Equal(1, stale.Groups["cold"].TraceCount);
        AssertMetric(stale.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 1, p50: 100.0);
        Assert.NotEqual(initial.GeneratedAt, stale.GeneratedAt);
    }

    [Fact]
    public void StaleCache_DoesNotMutateOriginalSummary()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);

        var shouldFail = false;
        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            openFile: path => shouldFail
                ? throw new IOException("simulated read error")
                : RollingJsonlPerformanceSummaryProvider.OpenFileWithSharedDelete(path),
            cacheTtl: TimeSpan.Zero);

        var initial = provider.GetSummary();
        shouldFail = true;
        provider.GetSummary();

        Assert.Equal("available", initial.Status);
        Assert.Equal("preliminary", initial.Groups["cold"].Quality);
        Assert.NotEqual("stale_snapshot", initial.Quality.ReasonCode);
    }

    [Fact]
    public void StaleCache_BoundaryPartial_DoesNotUseStale()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            cacheTtl: TimeSpan.Zero,
            maxLineBytes: 300);

        var first = provider.GetSummary();
        Assert.Equal("available", first.Status);
        Assert.Equal(1, first.Groups["cold"].TraceCount);

        // Add a new valid trace and a boundary trigger in the same refresh.
        WriteTrace("t2", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 10);
        var hugePad = new string('a', 2000);
        var longLine = "{\"schema_version\":1,\"trace_id\":\"x\",\"event\":\"intent.accepted\",\"timestamp_utc\":\"2026-07-18T00:00:00Z\",\"elapsed_from_intent_ms\":0,\"pad\":\"" + hugePad + "\"}";
        File.AppendAllText(BasePath(), longLine + "\n");

        var second = provider.GetSummary();

        Assert.Equal("degraded", second.Status);
        Assert.Equal("read_boundary_reached", second.Quality.ReasonCode);
        Assert.Equal(2, second.Groups["cold"].TraceCount);
        AssertMetric(second.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 2, p50: 100.0, p95: 200.0);
    }

    [Theory]
    [InlineData("warm", "cold")]
    [InlineData("cold", "warm")]
    public void ContextConflict_StartupKind_CrossFile_OrderIndependent(string baseKind, string historyKind)
    {
        var history = HistoryPath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.WriteAllLines(history, new[]
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: historyKind, ensureMs: 100, status: "consumed", timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc)),
            MakeEvent("t1", "capture.first_frame_observed", 200, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(200))
        });

        File.WriteAllLines(BasePath(), new[]
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: baseKind, ensureMs: 100, status: "consumed", timestampUtc: new DateTime(2026, 7, 18, 0, 0, 1, DateTimeKind.Utc))
        });

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.Equal(0, summary.Groups["warm"].TraceCount);
        Assert.Equal(1, summary.Quality.UnclassifiedTraceCount);
        Assert.True(summary.Quality.DiscardedSampleCount >= 1);
    }

    [Theory]
    [InlineData("consumed", "reused")]
    [InlineData("reused", "consumed")]
    public void ContextConflict_EnsureContextStatus_ConsumedReused(string baseStatus, string historyStatus)
    {
        var history = HistoryPath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.WriteAllLines(history, new[]
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: historyStatus, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc)),
            MakeEvent("t1", "capture.first_frame_observed", 200, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(200))
        });

        File.WriteAllLines(BasePath(), new[]
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: baseStatus, timestampUtc: new DateTime(2026, 7, 18, 0, 0, 1, DateTimeKind.Utc))
        });

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.Equal(1, summary.Quality.UnclassifiedTraceCount);
    }

    [Fact]
    public void ContextConflict_EnsureElapsed_DifferentValues()
    {
        var lines = new List<string>
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: "consumed"),
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 200, status: "consumed"),
            MakeEvent("t1", "capture.first_frame_observed", 200)
        };
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.Equal(1, summary.Quality.UnclassifiedTraceCount);
    }

    [Fact]
    public void ContextConflict_ServiceStartup_DifferentValues()
    {
        var lines = new List<string>
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, serviceStartupMs: 50, status: "consumed"),
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, serviceStartupMs: 150, status: "consumed"),
            MakeEvent("t1", "capture.first_frame_observed", 200)
        };
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.Equal(1, summary.Quality.UnclassifiedTraceCount);
    }

    [Fact]
    public void ContextConflict_RepeatedSameValues_NormalClassification()
    {
        var lines = new List<string>
        {
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, serviceStartupMs: 50, status: "consumed"),
            MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, serviceStartupMs: 50, status: "consumed"),
            MakeEvent("t1", "capture.first_frame_observed", 200)
        };
        File.WriteAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("available", summary.Status);
        Assert.Equal(1, summary.Groups["cold"].TraceCount);
        AssertMetric(summary.Groups["cold"], "ensure_running_ms", sampleCount: 1, p50: 100.0);
        AssertMetric(summary.Groups["cold"], "service_startup_ms", sampleCount: 1, p50: 50.0);
    }

    [Fact]
    public void ContextConflict_WithNormalTrace_KeepsNormalMetrics()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);

        var lines = new List<string>
        {
            MakeEvent("t2", "intent.accepted", 0, startupKind: "cold", ensureMs: 100, status: "consumed"),
            MakeEvent("t2", "intent.accepted", 0, startupKind: "warm", ensureMs: 100, status: "consumed"),
            MakeEvent("t2", "capture.first_frame_observed", 200)
        };
        File.AppendAllLines(BasePath(), lines);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(1, summary.Groups["cold"].TraceCount);
        Assert.Equal(0, summary.Groups["warm"].TraceCount);
        Assert.Equal(1, summary.Quality.UnclassifiedTraceCount);
        AssertMetric(summary.Groups["cold"], "request_to_first_frame_progress_ms", sampleCount: 1, p50: 100.0);
    }

    [Fact]
    public void Utf8LineBytes_AsciiAtLimitAndOver()
    {
        var utf8 = new UTF8Encoding(false);

        // 49 ASCII chars + LF = 50 bytes, exactly at the limit.
        var exactLine = new string('a', 49);
        File.WriteAllText(BasePath(), exactLine + "\n", utf8);

        var providerAtLimit = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxLineBytes: 50);
        var atLimit = providerAtLimit.GetSummary();
        Assert.NotEqual("read_boundary_reached", atLimit.Quality.ReasonCode);

        // 100 ASCII chars + LF = 101 bytes, well over the limit.
        File.WriteAllText(BasePath(), new string('b', 100) + "\n", utf8);
        var providerOver = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxLineBytes: 50);
        var over = providerOver.GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_MultibyteChars_BoundByBytes()
    {
        // 30 'é' = 60 UTF-8 bytes, 30 chars. With a small header this exceeds 50 bytes.
        var line = "{\"éééééééééééééééééééééééééééééééé\"}";
        File.WriteAllText(BasePath(), line + "\n");

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxLineBytes: 50);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_CrlfAndLf_ByteCounting()
    {
        var utf8 = new UTF8Encoding(false);

        // LF line: content 10 bytes + LF 1 byte = 11 bytes.
        var line = "0123456789";
        File.WriteAllText(BasePath(), line + "\n", utf8);

        var providerLf = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxTotalBytes: 10);
        var lf = providerLf.GetSummary();
        Assert.Equal("degraded", lf.Status);
        Assert.Equal("read_boundary_reached", lf.Quality.ReasonCode);

        // CRLF line: content 10 bytes + CRLF 2 bytes = 12 bytes.
        File.WriteAllText(BasePath(), line + "\r\n", utf8);
        var providerCrlf = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxTotalBytes: 11);
        var crlf = providerCrlf.GetSummary();
        Assert.Equal("degraded", crlf.Status);
        Assert.Equal("read_boundary_reached", crlf.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_LastLineWithoutNewline()
    {
        // 10 bytes, no newline. Should not invent an extra byte.
        var line = "0123456789";
        File.WriteAllText(BasePath(), line, new UTF8Encoding(false));

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxTotalBytes: 10);
        var summary = provider.GetSummary();

        // No boundary because the line is exactly 10 bytes; it is malformed.
        Assert.Equal("degraded", summary.Status);
        Assert.NotEqual("read_boundary_reached", summary.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_InvalidUtf8_IsolatedAndDegraded()
    {
        // 0xFF is invalid UTF-8. The reader should isolate it and the line is malformed.
        var bytes = Encoding.UTF8.GetBytes("{\"schema_version\":1,\"trace_id\":\"t1\"").Concat(new byte[] { 0xFF, (byte)'\n' }).ToArray();
        File.WriteAllBytes(BasePath(), bytes);

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.True(summary.Quality.MalformedLineCount >= 1);
    }

    [Fact]
    public void Utf8LineBytes_AsciiWithLf_Boundary()
    {
        var utf8 = new UTF8Encoding(false);

        // 49 ASCII + LF = 50 bytes total, exactly at the line limit.
        File.WriteAllText(BasePath(), new string('a', 49) + "\n", utf8);
        var atLimit = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.NotEqual("read_boundary_reached", atLimit.Quality.ReasonCode);

        // 50 ASCII + LF = 51 bytes total, one byte over the line limit.
        File.WriteAllText(BasePath(), new string('a', 50) + "\n", utf8);
        var over = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_Crlf_Boundary()
    {
        var utf8 = new UTF8Encoding(false);

        // 48 ASCII + CRLF = 50 bytes total, exactly at the line limit.
        File.WriteAllText(BasePath(), new string('a', 48) + "\r\n", utf8);
        var atLimit = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.NotEqual("read_boundary_reached", atLimit.Quality.ReasonCode);

        // 49 ASCII + CRLF = 51 bytes total, one byte over the line limit.
        File.WriteAllText(BasePath(), new string('a', 49) + "\r\n", utf8);
        var over = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_NoNewline_Boundary()
    {
        var utf8 = new UTF8Encoding(false);

        // 50 ASCII with no terminator = 50 body bytes, exactly at the limit.
        File.WriteAllText(BasePath(), new string('a', 50), utf8);
        var atLimit = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.NotEqual("read_boundary_reached", atLimit.Quality.ReasonCode);

        // 51 ASCII with no terminator = 51 body bytes, over the limit.
        File.WriteAllText(BasePath(), new string('a', 51), utf8);
        var over = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_MultibyteChar_TwoByteUtf8_Boundary()
    {
        var utf8 = new UTF8Encoding(false);

        // 25 'é' = 50 UTF-8 body bytes, no terminator, exactly at the limit.
        File.WriteAllText(BasePath(), new string('é', 25), utf8);
        var atLimit = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.NotEqual("read_boundary_reached", atLimit.Quality.ReasonCode);

        // 26 'é' = 52 UTF-8 body bytes, over the limit.
        File.WriteAllText(BasePath(), new string('é', 26), utf8);
        var over = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50).GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_Bom_NotCountedInLineLimit()
    {
        var utf8 = new UTF8Encoding(false);

        // BOM (3) + 49 ASCII + LF (1) = 53 file bytes, but the line body+LF is 50 bytes.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(utf8.GetBytes(new string('a', 49) + "\n"))
            .ToArray();
        File.WriteAllBytes(BasePath(), bytes);

        var provider = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxLineBytes: 50);
        var summary = provider.GetSummary();

        Assert.NotEqual("read_boundary_reached", summary.Quality.ReasonCode);
        Assert.True(summary.Quality.MalformedLineCount >= 1, "The non-JSON line should still be processed, not rejected by the line boundary.");
    }

    [Fact]
    public void Utf8LineBytes_Bom_DecodedLineDoesNotContainBom()
    {
        var utf8 = new UTF8Encoding(false);
        var line = MakeEvent("t1", "intent.accepted", 0, startupKind: "cold", ensureMs: 10, status: "consumed");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(utf8.GetBytes(line + "\n"))
            .ToArray();
        File.WriteAllBytes(BasePath(), bytes);

        var provider = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath());
        var summary = provider.GetSummary();

        Assert.Equal("available", summary.Status);
        Assert.Equal(1, summary.Groups["cold"].TraceCount);
        Assert.Equal(0, summary.Quality.MalformedLineCount);
    }

    [Fact]
    public void Utf8LineBytes_Bom_CountedInTotalBytes()
    {
        var utf8 = new UTF8Encoding(false);

        // BOM (3) + 'a' (1) + LF (1) = 5 bytes total.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n' };
        File.WriteAllBytes(BasePath(), bytes);

        var providerOver = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 4);
        var over = providerOver.GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);

        File.WriteAllBytes(BasePath(), bytes);
        var providerAt = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 5);
        var at = providerAt.GetSummary();
        Assert.NotEqual("read_boundary_reached", at.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_BomOnly_ExceedsTotalBoundary()
    {
        // A file that contains only a UTF-8 BOM must still count those bytes.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(BasePath(), bytes);

        var provider = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 2);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
        Assert.Equal(0, summary.Quality.MalformedLineCount);
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.Equal(0, summary.Groups["warm"].TraceCount);
    }

    [Fact]
    public void Utf8LineBytes_BomOnly_AtBoundary_IsNoData()
    {
        // BOM exactly equals the total-byte limit; no logical line exists.
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(BasePath(), bytes);

        var provider = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 3);
        var summary = provider.GetSummary();

        Assert.Equal("no_data", summary.Status);
        Assert.NotEqual("read_boundary_reached", summary.Quality.ReasonCode);
        Assert.Equal(0, summary.Quality.MalformedLineCount);
        Assert.Equal(0, summary.Quality.UnsupportedSchemaCount);
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.Equal(0, summary.Groups["warm"].TraceCount);
    }

    [Fact]
    public void Utf8LineBytes_LoneCr_LookaheadNotDoubleCounted()
    {
        var utf8 = new UTF8Encoding(false);
        // "{}\r{}" = 5 bytes. The CR lookahead byte must be counted exactly once.
        var bytes = utf8.GetBytes("{}\r{}");
        File.WriteAllBytes(BasePath(), bytes);

        var providerAt = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 5);
        var at = providerAt.GetSummary();
        Assert.NotEqual("read_boundary_reached", at.Quality.ReasonCode);

        File.WriteAllBytes(BasePath(), bytes);
        var providerOver = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 4);
        var over = providerOver.GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_MultipleLoneCrs_NotDoubleCounted()
    {
        var utf8 = new UTF8Encoding(false);
        // "a\rb\rc" = 5 bytes. Each lookahead byte belongs to the current line.
        var bytes = utf8.GetBytes("a\rb\rc");
        File.WriteAllBytes(BasePath(), bytes);

        var providerAt = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 5);
        var at = providerAt.GetSummary();
        Assert.NotEqual("read_boundary_reached", at.Quality.ReasonCode);

        File.WriteAllBytes(BasePath(), bytes);
        var providerOver = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 4);
        var over = providerOver.GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void Utf8LineBytes_Crlf_CountsTwoBytesAndNoDoubleCount()
    {
        var utf8 = new UTF8Encoding(false);
        // "a\r\nb" = 4 bytes. CRLF must count as 2 terminator bytes.
        var bytes = utf8.GetBytes("a\r\nb");
        File.WriteAllBytes(BasePath(), bytes);

        var providerAt = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 4);
        var at = providerAt.GetSummary();
        Assert.NotEqual("read_boundary_reached", at.Quality.ReasonCode);

        File.WriteAllBytes(BasePath(), bytes);
        var providerOver = new RollingJsonlPerformanceSummaryProvider(basePath: BasePath(), maxTotalBytes: 3);
        var over = providerOver.GetSummary();
        Assert.Equal("degraded", over.Status);
        Assert.Equal("read_boundary_reached", over.Quality.ReasonCode);
    }

    [Fact]
    public void DistinctTraceLimit_EnforcedBeforeProcessing()
    {
        WriteTrace("t1", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) }, ensureMs: 10);
        WriteTrace("t2", "cold", new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 200.0) }, ensureMs: 10);
        WriteTrace("t3", "cold", new[] { ("intent.accepted", 0.0) }, ensureMs: 10);

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxTotalTraces: 2);

        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
        Assert.Equal(2, summary.Groups["cold"].TraceCount);
    }

    [Fact]
    public void EventLineLimit_EnforcedBeforeProcessing()
    {
        // One trace spread across many lines; the event-line boundary should
        // stop processing before the trace is complete.
        for (int i = 0; i < 10; i++)
        {
            File.AppendAllText(BasePath(), MakeEvent("t1", "intent.accepted", i) + "\n");
        }

        var provider = new RollingJsonlPerformanceSummaryProvider(
            basePath: BasePath(),
            maxEventLines: 3);

        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal("read_boundary_reached", summary.Quality.ReasonCode);
    }

    [Fact]
    public void ContextMs_ExceedsTwoHours_Discarded()
    {
        var huge = 2L * 60 * 60 * 1000 + 1;
        WriteTrace("t1", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("capture.first_frame_observed", 100.0)
        }, ensureMs: huge, serviceStartupMs: huge, status: "consumed");

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.True(summary.Quality.DiscardedSampleCount >= 2);
        Assert.False(summary.Groups["cold"].Metrics.ContainsKey("ensure_running_ms"));
        Assert.False(summary.Groups["cold"].Metrics.ContainsKey("service_startup_ms"));
    }

    [Fact]
    public void ServiceStartup_Invalid_DiscardedButTraceStillGrouped()
    {
        var huge = 2L * 60 * 60 * 1000 + 1;
        WriteTrace("t1", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("capture.first_frame_observed", 100.0)
        }, ensureMs: 50, serviceStartupMs: huge, status: "consumed");

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(1, summary.Groups["cold"].TraceCount);
        AssertMetric(summary.Groups["cold"], "ensure_running_ms", sampleCount: 1, p50: 50.0);
        Assert.False(summary.Groups["cold"].Metrics.ContainsKey("service_startup_ms"));
        Assert.True(summary.Quality.DiscardedSampleCount >= 1);
    }

    [Fact]
    public void EnsureElapsed_Invalid_DiscardedAndUnclassified()
    {
        WriteTrace("t1", "cold", new[]
        {
            ("intent.accepted", 0.0),
            ("capture.first_frame_observed", 100.0)
        }, ensureMs: -5, serviceStartupMs: 50, status: "consumed");

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var summary = provider.GetSummary();

        Assert.Equal("degraded", summary.Status);
        Assert.Equal(0, summary.Groups["cold"].TraceCount);
        Assert.True(summary.Quality.DiscardedSampleCount >= 1);
        Assert.True(summary.Quality.UnclassifiedTraceCount >= 1);
    }

    [Fact]
    public void ApiServer_DefaultProvider_ReturnsCapabilitiesWithPerfSummary()
    {
        var engine = CreateMinimalEngine();
        var audit = new AuditLogger();
        var tray = new HeadlessTrayContext(audit);
        var server = new ApiServer(engine, audit, tray);

        var json = server.GetType()
            .GetMethod("Capabilities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(server, null) as object;
        var serialized = JsonSerializer.Serialize(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        using var doc = JsonDocument.Parse(serialized);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("perf_summary", out var perfSummary));
        Assert.True(perfSummary.TryGetProperty("status", out var status));
        Assert.Equal("no_data", status.GetString());
        Assert.True(perfSummary.TryGetProperty("groups", out var groups));
        Assert.True(groups.TryGetProperty("cold", out _));
        Assert.True(groups.TryGetProperty("warm", out _));
    }

    [Fact]
    public void ApiServer_FakeProvider_SerializesPerfSummary()
    {
        var engine = CreateMinimalEngine();
        var audit = new AuditLogger();
        var tray = new HeadlessTrayContext(audit);
        var fake = new FakePerformanceSummaryProvider();
        var server = new ApiServer(engine, audit, tray, performanceSummaryProvider: fake);

        var json = server.GetType()
            .GetMethod("Capabilities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(server, null) as object;
        var serialized = JsonSerializer.Serialize(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        using var doc = JsonDocument.Parse(serialized);
        Assert.Equal("available", doc.RootElement.GetProperty("perf_summary").GetProperty("status").GetString());
        Assert.Equal(42.0, doc.RootElement.GetProperty("perf_summary").GetProperty("groups").GetProperty("cold").GetProperty("metrics").GetProperty("ensure_running_ms").GetProperty("p50").GetDouble());
    }

    [Fact]
    public void Concurrency_MultipleReadersDoNotCorrupt()
    {
        for (int i = 0; i < 20; i++)
        {
            WriteTrace($"t{i}", i % 2 == 0 ? "cold" : "warm",
                new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", 100.0) },
                ensureMs: 50);
        }

        var provider = new RollingJsonlPerformanceSummaryProvider(_tempDir);
        var results = new PerformanceSummary[10];
        Parallel.For(0, 10, i => results[i] = provider.GetSummary());

        foreach (var r in results)
        {
            Assert.True(r.Groups.ContainsKey("cold"));
            Assert.True(r.Groups.ContainsKey("warm"));
        }
    }

    private string BasePath() => Path.Combine(_perfDir, "recording-traces.jsonl");
    private string HistoryPath(int index) => Path.Combine(_perfDir, $"recording-traces.{index}.jsonl");

    private void WriteTrace(string traceId, string? startupKind, (string Event, double Elapsed)[] events,
        long ensureMs = 100, long? serviceStartupMs = null, string status = "consumed", DateTime? timestamp = null)
    {
        var baseTime = timestamp ?? new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>();
        for (int i = 0; i < events.Length; i++)
        {
            var ev = events[i];
            var ts = baseTime.AddMilliseconds(ev.Elapsed);
            lines.Add(MakeEvent(traceId, ev.Event, ev.Elapsed, ts,
                startupKind: i == 0 ? startupKind : null,
                ensureMs: i == 0 ? ensureMs : null,
                serviceStartupMs: i == 0 ? serviceStartupMs : null,
                status: i == 0 ? status : null));
        }
        File.AppendAllLines(BasePath(), lines);
    }

    private void WriteColdTraces(double[] firstFrameLatencies)
    {
        for (int i = 0; i < firstFrameLatencies.Length; i++)
        {
            WriteTrace($"t{i}", "cold",
                new[] { ("intent.accepted", 0.0), ("capture.first_frame_observed", firstFrameLatencies[i]) },
                ensureMs: 50);
        }
    }

    private static string MakeEvent(string traceId, string eventName, double elapsedMs, DateTime? timestampUtc = null,
        string? startupKind = null, long? ensureMs = null, long? serviceStartupMs = null, string? status = null,
        Dictionary<string, object?>? clientHints = null)
    {
        var ts = timestampUtc ?? new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(elapsedMs);
        var obj = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["trace_id"] = traceId,
            ["event"] = eventName,
            ["timestamp_utc"] = ts.ToString("O"),
            ["elapsed_from_intent_ms"] = elapsedMs
        };
        if (startupKind != null) obj["startup_kind"] = startupKind;
        if (ensureMs.HasValue) obj["ensure_elapsed_ms"] = ensureMs.Value;
        if (serviceStartupMs.HasValue) obj["service_startup_elapsed_ms"] = serviceStartupMs.Value;
        if (status != null) obj["ensure_context_status"] = status;
        if (clientHints != null) obj["client_hints"] = clientHints;
        return JsonSerializer.Serialize(obj);
    }

    private static void AssertMetric(PerformanceSummaryGroup group, string name, int sampleCount, double? p50 = null, double? p95 = null)
    {
        Assert.True(group.Metrics.ContainsKey(name), $"Expected metric {name}");
        var m = group.Metrics[name];
        Assert.Equal(sampleCount, m.SampleCount);
        if (p50.HasValue) Assert.Equal(p50.Value, m.P50, precision: 1);
        if (p95.HasValue) Assert.Equal(p95.Value, m.P95, precision: 1);
    }

    private static RecordingEngine CreateMinimalEngine()
    {
        var audit = new AuditLogger();
        var tracer = NoOpPerformanceTracer.Instance;
        return new RecordingEngine(audit, tracer);
    }

    private sealed class FakePerformanceSummaryProvider : IPerformanceSummaryProvider
    {
        public PerformanceSummary GetSummary() => new()
        {
            SchemaVersion = 1,
            Status = PerformanceSummaryStatus.Available,
            GeneratedAt = DateTime.UtcNow,
            Window = new PerformanceSummaryWindow { MaxTracesPerGroup = 50 },
            Quality = new PerformanceSummaryQuality(),
            Groups = new Dictionary<string, PerformanceSummaryGroup>
            {
                ["cold"] = new()
                {
                    TraceCount = 1,
                    Quality = PerformanceSummaryQualityLabels.Preliminary,
                    Metrics = new Dictionary<string, PerformanceSummaryMetric>
                    {
                        ["ensure_running_ms"] = new() { SampleCount = 1, P50 = 42.0, P95 = 42.0 }
                    }
                },
                ["warm"] = new()
                {
                    TraceCount = 0,
                    Quality = PerformanceSummaryQualityLabels.Preliminary,
                    Metrics = new Dictionary<string, PerformanceSummaryMetric>()
                }
            }
        };
    }
}
