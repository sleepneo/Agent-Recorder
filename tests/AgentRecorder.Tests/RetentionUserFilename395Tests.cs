using AgentRecorder.Windows;
using AgentRecorder.Core.Automation;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionUserFilename395Tests(ITestOutputHelper output)
{
    public sealed class NativeUserNameTheoryAttribute : TheoryAttribute
    {
        public NativeUserNameTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TASK395_NATIVE_PARENT"))) Skip = "Supply an owned Windows fixture parent with real short names.";
        }
    }
    [NativeUserNameTheory]
    [InlineData("required", "leaf")]
    [InlineData("standing", "leaf")]
    [InlineData("required", "root")]
    [InlineData("standing", "root")]
    [InlineData("required", "both")]
    [InlineData("standing", "both")]
    [InlineData("required", "canonical")]
    [InlineData("standing", "canonical")]
    public async Task RealCreationPreservesUserLeafAndProtectsNewObject(string source, string mode)
    {
        string alias = "";
        string requested = "";
        ObservedFileIdentity? original = null;
        using var local = new LocalRetentionDeletionFixture(real: true,
            fixtureParent: Environment.GetEnvironmentVariable("TASK395_NATIVE_PARENT"),
            mediaName: "Long disposable video filename.mp4",
            beforeMedia: (db, media) =>
            {
                File.WriteAllBytes(media, Enumerable.Range(0, 12).Select(i => (byte)i).ToArray());
                var probe = WindowsReadOnlyFileMetadataProbe.Instance.ObserveRetention(media);
                original = probe.After!.Identity;
                alias = Path.Combine(Path.GetDirectoryName(media)!, probe.ShortLeaf!);
                Assert.True(WindowsShortFileName.TryGetAliasPath(db.Root, probe.DataRootShortLeaf, out var rootAlias));
                Assert.NotNull(rootAlias);
                var directory = mode is "root" or "both"
                    ? Path.Combine(rootAlias, "Videos") : Path.GetDirectoryName(media)!;
                var filename = mode is "leaf" or "both" ? probe.ShortLeaf! : Path.GetFileName(media);
                requested = Path.Combine(directory, filename);
                Assert.NotEqual(media, alias);
                Assert.False(SystemQueryStandingLeaseOutputReadinessProvider.Instance.Check(
                    Path.GetDirectoryName(media)!, probe.ShortLeaf!, TimeSpan.FromSeconds(60)).IsReady);
                var retained = Path.Combine(db.Root, "retained-original");
                Directory.CreateDirectory(retained); File.Move(media, Path.Combine(retained, Path.GetFileName(media)));
                Assert.False(File.Exists(alias));
                (source == "required" ? CreateRequired(db, requested) : CreateStanding(db, requested)).GetAwaiter().GetResult();
            });
        Assert.NotEqual(original, local.Anchor.Identity);
        Assert.Equal(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias).After!.Identity);
        using (var c = local.Db.Store.OpenReadOnlyConnection())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT output_directory,frozen_file_name FROM " + (source == "required" ? "required_once_authorized_specs" : "authorized_capture_scopes");
            using var r = cmd.ExecuteReader(); Assert.True(r.Read());
            Assert.Equal(Path.GetFileName(requested), r.GetString(1));
            Assert.Equal(local.Videos, r.GetString(0).TrimEnd(Path.DirectorySeparatorChar), ignoreCase: true);
            output.WriteLine($"requested={requested}\nstoredDirectory={r.GetString(0)} storedLeaf={r.GetString(1)}");
        }
        var preview = new RetentionPreviewQueryService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None);
        Assert.Equal("planned_output_path", Assert.Single(preview.GetPage(Sid, Session, 1, At.AddHours(1)).Items).ReasonCode);
        var eval = await new RetentionEvaluationService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None)
            .ReadAsync([local.Row.RecordingId], At.AddHours(1), Sid, Session);
        Assert.Equal("planned_output_path", Assert.Single(eval!.Page!.Items).ReasonCode);

        using var service = local.Service(); var pending = service.Request(local.Row.RecordingId); var permit = await local.Approve(service, pending);
        var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
        Assert.Equal(0, local.Native.Calls); Assert.Equal(LocalDeletionProgress.NotAttempted, result.Progress);
        output.WriteLine($"alias={alias} identity={local.Anchor.Identity} preview=planned_output_path byId=planned_output_path calls={local.Native.Calls} OS=not-attempted");
    }
    [NativeUserNameTheory]
    [InlineData("required")]
    [InlineData("standing")]
    public async Task LegalReferenceCreatedAfterApprovalStopsOriginalDisposition(string source)
    {
        using var local = new LocalRetentionDeletionFixture(real: true, fixtureParent: Environment.GetEnvironmentVariable("TASK395_NATIVE_PARENT"), mediaName: "Long disposable late video.mp4");
        var sample = WindowsReadOnlyFileMetadataProbe.Instance.ObserveRetention(local.Media);
        var alias = Path.Combine(local.Videos, sample.ShortLeaf!);
        var evaluation = new RetentionEvaluationService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None);
        Assert.Equal("age_rule_match", Assert.Single((await evaluation.ReadAsync([local.Row.RecordingId], At.AddHours(1), Sid, Session))!.Page!.Items).ReasonCode);
        using var service = local.Service(); var pending = service.Request(local.Row.RecordingId); var permit = await local.Approve(service, pending);
        var retained = Path.Combine(local.Db.Root, "held"); Directory.CreateDirectory(retained);
        var held = Path.Combine(retained, Path.GetFileName(local.Media));
        File.Move(local.Media, held); Assert.False(File.Exists(alias));
        await (source == "required" ? CreateRequired(local.Db, alias) : CreateStanding(local.Db, alias));
        File.Move(held, local.Media);
        Assert.Equal(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias).After!.Identity);
        var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
        Assert.Equal(0, local.Native.Calls); Assert.Equal(LocalDeletionProgress.NotAttempted, result.Progress);
        Assert.Equal(local.Bytes, File.ReadAllBytes(local.Media));
    }
    [Fact]
    public async Task AliasQueriesReuseTupleIndexesAndKeepDamagedRelationsProtected()
    {
        using var f = RetentionPlannedOutputPathTests.Fixture();
        var path = await RetentionPlannedOutputPathTests.Purpose(f, "standing");
        f.Db.Corrupt("UPDATE authorized_capture_scopes SET scope_digest='damaged';");
        var requested = Enumerable.Range(0, 100).ToDictionary(i => "id" + i,
            _ => (IReadOnlyList<string>)new[] { path, path + "-other", path + "-third" });
        var queries = 0;
        f.Query.RetentionPlannedPathCommandForTests = (c, t, sql, args) =>
        {
            queries++;
            Assert.Equal(500, args.Count);
            Assert.DoesNotContain("future_window_authorizations", sql);
            Assert.DoesNotContain("recurring_occurrence_execution_specs", sql);
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var arg in args) command.Parameters.AddWithValue(arg.Key, arg.Value);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, p => p.Contains("SEARCH frozen USING COVERING INDEX idx_authorized_capture_scopes_planned_output_path"));
            Assert.Contains(plan, p => p.Contains("SEARCH frozen USING COVERING INDEX idx_required_once_authorized_specs_planned_output_path"));
            Assert.DoesNotContain(plan, p => p.Contains("SCAN frozen"));
            if (queries == 1) output.WriteLine(string.Join("\n", plan));
        };
        var references = f.Query.ReadShortNameReferences(requested, CancellationToken.None);
        Assert.All(references.Values, value => { Assert.True(value.Planned); Assert.False(value.Shared); });
        Assert.Equal(3, queries);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("schema")]
    [InlineData("path")]
    public void UnconfirmedAliasPlanEvidenceCannotBecomeNoReference(string failure)
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        var requested = new Dictionary<string, IReadOnlyList<string>>
        {
            ["candidate"] = new[] { failure == "path" ? "invalid" : Path.Combine(f.Videos, "SHORT.MP4") }
        };
        if (failure == "schema") f.Db.Execute("DROP INDEX idx_required_once_authorized_specs_planned_output_path;");
        if (failure == "query") f.Db.Query.RetentionPlannedPathCommandForTests = (_, _, _, _) => throw new IOException("Owned failure");
        Assert.ThrowsAny<Exception>(() => f.Db.Query.ReadShortNameReferences(requested, CancellationToken.None));
    }

    internal static async Task CreateRequired(UnifiedHistoryTestDatabase db, string path)
    {
        var display = new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display", DisplayIdentityResolutionStatus.Resolved, new(0, 0, 1920, 1080), 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape);
        using var setup = new RequiredOncePlanSetupCoordinator(db.Store, new RetentionSharedOutputPathTests.QuietAudit(), new Ui(),
            currentDisplaysForTest: () => new[] { display }, principalForTest: () => new RequiredOncePrincipal("foreign-sid", "foreign-session"), clockForTest: () => At);
        var request = StandingPlanApiRequestParser.ParseRequired(RequestBody(path, At, required: true), Guid.NewGuid().ToString());
        Assert.Equal(Path.GetFileName(path), request.FrozenFileName);
        var created = setup.CreateOrGet(request);
        await setup.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("scheduled", setup.Get(created.SetupIntentId!)!.StatusCode);
    }
    internal static async Task CreateStanding(UnifiedHistoryTestDatabase db, string path)
    {
        var display = new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display", DisplayIdentityResolutionStatus.Resolved, new(0, 0, 1920, 1080), 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape);
        using var setup = new StandingPlanSetupCoordinator(db.Store, new RetentionSharedOutputPathTests.QuietAudit(), new Ui(), new StandingLeaseSafetyControlService(db.Store), () => new[] { display });
        var now = DateTimeOffset.UtcNow;
        var request = StandingPlanApiRequestParser.Parse(RequestBody(path, now, required: false), Guid.NewGuid().ToString());
        Assert.Equal(Path.GetFileName(path), request.FrozenFileName);
        var created = setup.CreateOrGet(request);
        await setup.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var state = setup.Get(created.SetupIntentId!);
        Assert.Equal("scheduled", state!.StatusCode);
    }
    internal static string RequestBody(string path, DateTimeOffset now, bool required)
    {
        object authorization = required ? new { mode = "required" } : (object)new
        {
            mode = "standing_lease", expires_at = now.AddMinutes(10), max_runs = 1, max_duration_seconds = 60
        };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            recording_spec = new
            {
                source = new { type = "fixed_region" }, audio = new { mode = "none" },
                duration_seconds = 60, countdown_seconds = 0, backend = "ffmpeg-region",
                output = new { directory = Path.GetDirectoryName(path), filename = Path.GetFileName(path) }
            },
            schedule = new { kind = "once", start_at = now.AddMinutes(2), latest_start_at = now.AddMinutes(3), planned_end_at = now.AddMinutes(6) },
            requested_authorization = authorization
        });
    }
    internal sealed class Ui : IRequiredOncePlanSetupUi, IStandingPlanSetupUi
    {
        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails d, CancellationToken t) => Task.FromResult(true);
        public Task ShowFailureAsync(string r, CancellationToken t) => Task.CompletedTask;
        public bool IsInteractiveDesktopAvailable => true;
        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken t) => Task.FromResult(new StandingPlanSetupSelection("selected", 120, 140, 640, 360, "DISPLAY-1", "virtual_screen"));
        public Task<RequiredOnceCreationApprovalResult> ShowCreationApprovalAsync(RequiredOnceCreationApprovalDetails d, CancellationToken t) => Task.FromResult(RequiredOnceCreationApprovalResult.Approved);
    }
}
