using AgentRecorder.Api;
using AgentRecorder.Windows;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionPendingAlias396Tests(ITestOutputHelper output)
{
    public sealed class NativeTheoryAttribute : TheoryAttribute
    {
        public NativeTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TASK396_NATIVE_PARENT")))
                Skip = "Supply an owned Windows fixture parent with real short names.";
        }
    }
    [NativeTheory]
    [InlineData("required", "leaf", false)][InlineData("standing", "leaf", false)]
    [InlineData("required", "both", true)][InlineData("standing", "both", true)]
    [InlineData("required", "canonical", false)][InlineData("standing", "canonical", false)]
    [InlineData("required", "root", true)][InlineData("standing", "root", true)]
    public async Task ProductionPendingTupleProtectsOriginalObject(string source, string mode, bool approval)
    {
        Flight? flight = null;
        string alias = "";
        ObservedFileIdentity? oldIdentity = null;
        try
        {
            using var local = new LocalRetentionDeletionFixture(real: true,
                fixtureParent: Environment.GetEnvironmentVariable("TASK396_NATIVE_PARENT"),
                mediaName: "Long pending disposable video filename.mp4", beforeMedia: (db, media) =>
                {
                    File.WriteAllBytes(media, new byte[12]);
                    var observed = WindowsReadOnlyFileMetadataProbe.Instance.ObserveRetention(media);
                    oldIdentity = observed.After!.Identity;
                    Assert.True(WindowsShortFileName.TryGetAliasPath(db.Root, observed.DataRootShortLeaf, out var root));
                    Assert.NotNull(root);
                    alias = Path.Combine(mode is "root" or "both" ? Path.Combine(root, "Videos") : Path.GetDirectoryName(media)!,
                        mode is "leaf" or "both" ? observed.ShortLeaf! : Path.GetFileName(media));
                    Assert.False(SystemQueryStandingLeaseOutputReadinessProvider.Instance.Check(
                        Path.GetDirectoryName(alias)!, Path.GetFileName(alias), TimeSpan.FromSeconds(60)).IsReady);
                    var retained = Path.Combine(db.Root, "retained-original"); Directory.CreateDirectory(retained);
                    File.Move(media, Path.Combine(retained, Path.GetFileName(media)));
                    flight = Flight.Start(db, alias, source, approval);
                    flight.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    flight.AssertTuple(db, Path.GetDirectoryName(media)!, Path.GetFileName(alias), output);
                });
            Assert.NotEqual(oldIdentity, local.Anchor.Identity);
            Assert.Equal(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias).After!.Identity);
            var baseline = Environment.GetEnvironmentVariable("TASK396_BASELINE") == "1";
            var reason = source == "standing" && approval ? "planned_output_path" :
                baseline && mode is "leaf" or "both" ? "age_rule_match" : "setup_output_path";
            var page = new RetentionPreviewQueryService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None)
                .GetPage(Sid, Session, 1, At.AddHours(1));
            Assert.Equal(reason, Assert.Single(page.Items).ReasonCode);
            var evaluated = await new RetentionEvaluationService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None)
                .ReadAsync([local.Row.RecordingId], At.AddHours(1), Sid, Session);
            Assert.Equal(reason, Assert.Single(evaluated!.Page!.Items).ReasonCode);
            using var service = local.Service(); var pending = service.Request(local.Row.RecordingId);
            var permit = await local.Approve(service, pending);
            if (baseline) local.Native.BeforeRequest = _ => throw new IOException("Owned baseline intercept before OS disposition");
            var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
            Assert.Equal(baseline && reason == "age_rule_match" ? 1 : 0, local.Native.Calls);
            if (!baseline || reason != "age_rule_match") Assert.Equal(LocalDeletionProgress.NotAttempted, result.Progress);
            Assert.Equal(local.Bytes, File.ReadAllBytes(local.Media));
            output.WriteLine($"source={source} mode={mode} approval={approval} alias={alias} reason={reason} calls={local.Native.Calls} identity={local.Anchor.Identity}");
            flight!.Ui.Release.TrySetResult();
            await flight.Wait();
            Assert.Equal(source == "standing" ? "planned_output_path" : reason, Assert.Single(new RetentionPreviewQueryService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None)
                .GetPage(Sid, Session, 1, At.AddHours(1)).Items).ReasonCode);
        }
        finally { if (flight is not null) await flight.Stop(); }
    }

    [NativeTheory]
    [InlineData("required", false)][InlineData("required", true)][InlineData("standing", false)]
    public async Task PendingReferenceArrivingAfterDeletionApprovalStopsDisposition(string source, bool approval)
    {
        using var local = new LocalRetentionDeletionFixture(real: true,
            fixtureParent: Environment.GetEnvironmentVariable("TASK396_NATIVE_PARENT"), mediaName: "Long pending late video.mp4");
        var observed = WindowsReadOnlyFileMetadataProbe.Instance.ObserveRetention(local.Media);
        var alias = Path.Combine(local.Videos, observed.ShortLeaf!);
        using var service = local.Service(); var pending = service.Request(local.Row.RecordingId);
        var permit = await local.Approve(service, pending);
        var directory = Path.Combine(local.Db.Root, "temporarily-held"); Directory.CreateDirectory(directory);
        var held = Path.Combine(directory, Path.GetFileName(local.Media)); File.Move(local.Media, held);
        using var flight = Flight.Start(local.Db, alias, source, approval);
        try
        {
            await flight.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            File.Move(held, local.Media);
            Assert.Equal(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias).After!.Identity);
            var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
            Assert.Equal(0, local.Native.Calls); Assert.Equal(LocalDeletionProgress.NotAttempted, result.Progress);
            Assert.Equal(local.Bytes, File.ReadAllBytes(local.Media));
        }
        finally { await flight.Stop(); }
    }

    [NativeTheory]
    [InlineData("required")][InlineData("standing")]
    public async Task ShortLeafOfDifferentDirectoryObjectDoesNotProtectCandidate(string source)
    {
        using var local = new LocalRetentionDeletionFixture(real: true,
            fixtureParent: Environment.GetEnvironmentVariable("TASK396_NATIVE_PARENT"), mediaName: "Long pending negative video.mp4");
        var otherDirectory = Path.Combine(local.Db.Root,"other-owned-directory"); Directory.CreateDirectory(otherDirectory);
        var other = Path.Combine(otherDirectory,Path.GetFileName(local.Media)); File.WriteAllBytes(other,local.Bytes);
        var observed = WindowsReadOnlyFileMetadataProbe.Instance.ObserveRetention(other);
        Assert.NotEqual(local.Anchor.Identity,observed.After!.Identity);
        Assert.False(string.IsNullOrEmpty(observed.ShortLeaf));
        var alias = Path.Combine(otherDirectory,observed.ShortLeaf!);
        var held = other + ".held"; File.Move(other,held);
        Assert.False(File.Exists(alias));
        using var flight = Flight.Start(local.Db,alias,source,false);
        try
        {
            await flight.Ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); File.Move(held,other);
            Assert.Equal(observed.After.Identity,WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias).After!.Identity);
            Assert.Equal("age_rule_match",Assert.Single(new RetentionPreviewQueryService(local.Db.Query,local.Db.Root,TestRuntimeOutputUse.None)
                .GetPage(Sid,Session,1,At.AddHours(1)).Items).ReasonCode);
            Assert.Equal(local.Bytes,File.ReadAllBytes(local.Media));
        }
        finally { await flight.Stop(); }
    }

    internal sealed class Flight : IDisposable
    {
        internal readonly BlockingUi Ui;
        private readonly IDisposable _setup;
        private readonly Func<Task> _idle;
        private readonly string _table;
        private Flight(BlockingUi ui, IDisposable setup, Func<Task> idle, string table)
        { Ui = ui; _setup = setup; _idle = idle; _table = table; }
        internal static Flight Start(UnifiedHistoryTestDatabase db, string path, string source, bool approval)
        {
            var ui = new BlockingUi(approval);
            var displays = new[] { new StandingLeaseDisplayMetadata("DISPLAY-1", "stable-display", DisplayIdentityResolutionStatus.Resolved,
                new(0, 0, 1920, 1080), 96, 96, 1920, 1080, AuthorizedDisplayOrientation.Landscape) };
            if (source == "required")
            {
                var setup = new RequiredOncePlanSetupCoordinator(db.Store, new RetentionSharedOutputPathTests.QuietAudit(), ui,
                    currentDisplaysForTest: () => displays, principalForTest: () => new RequiredOncePrincipal("foreign-sid", "foreign-session"), clockForTest: () => At);
                var request = StandingPlanApiRequestParser.ParseRequired(RetentionUserFilename395Tests.RequestBody(path, At, true), Guid.NewGuid().ToString());
                Assert.NotNull(setup.CreateOrGet(request).SetupIntentId);
                return new(ui, setup, setup.WaitForIdleAsync, "required_once_setup_intents");
            }
            else
            {
                var setup = new StandingPlanSetupCoordinator(db.Store, new RetentionSharedOutputPathTests.QuietAudit(), ui,
                    new StandingLeaseSafetyControlService(db.Store), () => displays);
                var request = StandingPlanApiRequestParser.Parse(RetentionUserFilename395Tests.RequestBody(path, DateTimeOffset.UtcNow, false), Guid.NewGuid().ToString());
                Assert.NotNull(setup.CreateOrGet(request).SetupIntentId);
                return new(ui, setup, setup.WaitForIdleAsync, "setup_intents");
            }
        }
        internal void AssertTuple(UnifiedHistoryTestDatabase db, string directory, string leaf, ITestOutputHelper log)
        {
            using var c = db.Store.OpenReadOnlyConnection(); using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT output_directory,frozen_file_name,status_code FROM " + _table;
            using var r = cmd.ExecuteReader(); Assert.True(r.Read());
            Assert.Equal(directory, r.GetString(0).TrimEnd('\\'), ignoreCase: true); Assert.Equal(leaf, r.GetString(1));
            Assert.Contains(r.GetString(2), new[] { "region_selection_pending", "creation_approval_pending", "lease_approval_pending" });
            log.WriteLine($"table={_table} directory={r.GetString(0)} leaf={r.GetString(1)} state={r.GetString(2)}");
        }
        internal async Task Stop() { _setup.Dispose(); await _idle().WaitAsync(TimeSpan.FromSeconds(10)); }
        internal Task Wait() => _idle().WaitAsync(TimeSpan.FromSeconds(10));
        public void Dispose() => _setup.Dispose();
    }
    internal sealed class BlockingUi(bool approval) : IRequiredOncePlanSetupUi, IStandingPlanSetupUi
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInteractiveDesktopAvailable => true;
        private async Task Block(CancellationToken t) { Entered.TrySetResult(); await Release.Task.WaitAsync(t); }
        public async Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken t)
        {
            if (!approval) await Block(t);
            return new("selected", 120, 140, 640, 360, "DISPLAY-1", "virtual_screen");
        }
        public async Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails d, CancellationToken t) { await Block(t); return false; }
        public async Task<RequiredOnceCreationApprovalResult> ShowCreationApprovalAsync(RequiredOnceCreationApprovalDetails d, CancellationToken t)
        { await Block(t); return RequiredOnceCreationApprovalResult.Rejected; }
        public Task ShowFailureAsync(string r, CancellationToken t) => Task.CompletedTask;
    }
}
