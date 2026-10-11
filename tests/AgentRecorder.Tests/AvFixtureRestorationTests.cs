using System.Net.Sockets;
using System.Reflection;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;
using Xunit.Sdk;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AvFixtureRestorationTests
{
    private sealed class Capacity : IStorageCapacityProvider
    {
        public Task<IReadOnlyList<StorageVolumeSample>> QueryAsync(IReadOnlyList<string> paths, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StorageVolumeSample>>(paths.Select(p => new StorageVolumeSample(p, "sentinel-volume", 100L << 30)).ToArray());
    }

    private sealed class Sentinel : IDisposable
    {
        internal static readonly string[] Keys = { "AGENT_RECORDER_TEST_MODE", "AGENT_RECORDER_DATA_DIR",
            CaptureBackendSelector.DisplayBackendEnvVar, CaptureBackendSelector.RegionBackendEnvVar };
        private readonly Dictionary<string, string?> _priorEnvironment = Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        private readonly IStorageCapacityProvider _priorCapacity = RecordingPreflightChecker.StorageCapacityProvider;
        private readonly RecordingPreflightChecker.TryResolveAudioHelper _priorHelper = RecordingPreflightChecker.AudioHelperPathResolver;
        private readonly string? _priorDir = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
        internal readonly string Root = Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task339", Guid.NewGuid().ToString("N"));
        internal readonly Capacity Provider = new();
        internal readonly FakeAudioHelperDeployment Helper;
        internal readonly RecordingPreflightChecker.TryResolveAudioHelper Resolver;
        internal readonly Dictionary<string, string?> EnvironmentValues;
        internal Sentinel()
        {
            Helper = new(Root);
            Resolver = () => Helper.ExecutablePath;
            EnvironmentValues = Keys.ToDictionary(k => k, k => (string?)(k == "AGENT_RECORDER_DATA_DIR" ? Path.Combine(Root, "environment") : "sentinel-" + k));
            RecordingPreflightChecker.StorageCapacityProvider = Provider;
            RecordingPreflightChecker.AudioHelperPathResolver = Resolver;
            DataDirResolver.SetOverride(Root);
            foreach (var pair in EnvironmentValues) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        internal void Restored()
        {
            Assert.Same(Provider, RecordingPreflightChecker.StorageCapacityProvider);
            Assert.Same(Resolver, RecordingPreflightChecker.AudioHelperPathResolver);
            Assert.True(DataDirResolver.HasOverride);
            Assert.Equal(Path.GetFullPath(Root), DataDirResolver.Resolve());
            foreach (var pair in EnvironmentValues) Assert.Equal(pair.Value, Environment.GetEnvironmentVariable(pair.Key));
        }
        public void Dispose()
        {
            try { Helper.Dispose(); }
            finally
            {
                RecordingPreflightChecker.StorageCapacityProvider = _priorCapacity;
                RecordingPreflightChecker.AudioHelperPathResolver = _priorHelper;
                if (_priorDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_priorDir);
                foreach (var pair in _priorEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private static T Field<T>(object fixture, string name) => (T)fixture.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(fixture)!;
    private static void AllReleased(OrdinaryAvStorageSafetyTests.FixtureFaults faults, bool expectGlobalReset = true)
    {
        Assert.Equal(faults.Acquired.AsEnumerable().Reverse(), faults.Released);
        Assert.Equal(faults.Released.Count, faults.Released.Distinct().Count());
        if (faults.Resources.TryGetValue("audio", out var audio)) Assert.Null(((FakeAudioCaptureWorker)audio).OutputPath);
        if (faults.Resources.TryGetValue("video", out var video)) Assert.Null(((FakeVideoCaptureWorker)video).OutputPath);
        if (faults.Resources.TryGetValue("api", out var api)) Assert.Equal(ApiListenerState.Stopped, ((ApiServer)api).ReadRuntimeObservation().ListenerState);
        if (expectGlobalReset && faults.Resources.ContainsKey("api"))
        {
            Assert.False(ApiKeyAuth.IsEnabled);
            var provider = (AsyncLocal<Func<List<SystemQuery.DisplayTopologyInfo>>?>)typeof(SystemQuery)
                .GetField("_displayTopologyProvider", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.Null(provider.Value);
        }
        if (faults.Resources.TryGetValue("engine", out var engine)) Assert.Empty(((RecordingEngine)engine)._recs);
        if (faults.Resources.TryGetValue("helper", out var helper)) Assert.False(Directory.Exists(((FakeAudioHelperDeployment)helper).Root));
    }
    private static async Task ExistingContractAfterFailure(Sentinel sentinel)
    {
        sentinel.Restored();
        Assert.True(File.Exists(RecordingPreflightChecker.AudioHelperPathResolver()));
        var sample = Assert.Single(await RecordingPreflightChecker.StorageCapacityProvider.QueryAsync(new[] { sentinel.Root }, CancellationToken.None));
        Assert.Equal(100L << 30, sample.AvailableBytes);
        // Invoke an unchanged admission/approval contract in this testhost, after
        // the failure, then verify its fixture restored the same sentinel again.
        await new OrdinaryAvStorageSafetyTests().ApprovalRechecksActualAvTempAndChangedOutput("microphone", true);
        sentinel.Restored();
    }

    [Fact]
    public async Task RealLockedHelperReportsFailureRestoresStateAndAllowsSameProcessContract()
    {
        using var sentinel = new Sentinel();
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults();
        var fixture = OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults);
        var helper = Field<FakeAudioHelperDeployment>(fixture, "_helper");
        var client = Field<HttpClient>(fixture, "Client");
        var port = Field<ApiServer>(fixture, "Api").BoundPort;
        var audio = Field<FakeAudioCaptureWorker>(fixture, "Audio");
        var video = Field<FakeVideoCaptureWorker>(fixture, "Video");
        try
        {
            using (var held = new FileStream(helper.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failure = Assert.Throws<XunitException>(() => fixture.Dispose());
                Assert.Contains("after bounded retries", failure.Message);
                Assert.Contains(helper.Root, failure.Message);
                sentinel.Restored();
                Assert.Equal(faults.Acquired.AsEnumerable().Reverse(), faults.Released);
                Assert.Equal(ApiListenerState.Stopped, Field<ApiServer>(fixture, "Api").ReadRuntimeObservation().ListenerState);
                Assert.Null(audio.OutputPath); Assert.Null(video.OutputPath);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("/api/v1/status"));
                using var socket = new TcpClient();
                await Assert.ThrowsAsync<SocketException>(() => socket.ConnectAsync("127.0.0.1", port));
                Assert.True(File.Exists(helper.ExecutablePath));
            }
            // Dispose consumes ownership once even on failure. Only this newly
            // created GUID helper directory is explicitly deleted after release.
            fixture.Dispose();
            TestDirectoryCleanup.DeleteOwnedDirectory(helper.Root);
            AllReleased(faults);
            await ExistingContractAfterFailure(sentinel);
            using var later = OrdinaryAvStorageSafetyTests.CreateFixtureForTests();
            var laterProvider = RecordingPreflightChecker.StorageCapacityProvider;
            var laterDir = DataDirResolver.Resolve();
            fixture.Dispose();
            Assert.Same(laterProvider, RecordingPreflightChecker.StorageCapacityProvider);
            Assert.Equal(laterDir, DataDirResolver.Resolve());
        }
        finally { fixture.Dispose(); }
    }

    [Theory]
    [InlineData("before-helper", 0)]
    [InlineData("after-helper", 1)]
    [InlineData("after-audio", 2)]
    [InlineData("after-backend", 4)]
    [InlineData("after-api-start", 7)]
    [InlineData("after-client", 8)]
    public async Task ConstructorFailureReleasesOnlyAcquiredResourcesAndRestores(string boundary, int acquired)
    {
        using var sentinel = new Sentinel();
        var original = new IOException("constructor: " + boundary);
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults {
            AtConstruction = phase => { if (phase == boundary) throw original; } };
        Assert.Same(original, Assert.Throws<IOException>(() => OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults)));
        Assert.Equal(acquired, faults.Acquired.Count);
        AllReleased(faults); sentinel.Restored();
        await ExistingContractAfterFailure(sentinel);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("api")]
    [InlineData("backend")]
    public async Task PriorReleaseFailureStillReleasesOthersAndRestores(string resource)
    {
        using var sentinel = new Sentinel();
        var original = new IOException("release: " + resource);
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults {
            AfterRelease = name => { if (name == resource) throw original; } };
        var fixture = OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults);
        Assert.Same(original, Assert.Throws<IOException>(() => fixture.Dispose()));
        AllReleased(faults); sentinel.Restored();
        fixture.Dispose(); AllReleased(faults);
        await ExistingContractAfterFailure(sentinel);
    }

    [Fact]
    public void ConstructionAndCleanupFailuresPreserveBothOriginalObjects()
    {
        using var sentinel = new Sentinel();
        var original = new IOException("constructor original");
        var cleanup = new IOException("cleanup original");
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults {
            AtConstruction = phase => { if (phase == "after-api-start") throw original; },
            AfterRelease = name => { if (name == "api") throw cleanup; } };
        var failure = Assert.Throws<AggregateException>(() => OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults));
        Assert.Equal(new Exception[] { original, cleanup }, failure.InnerExceptions);
        AllReleased(faults); sentinel.Restored();
    }

    [Fact]
    public async Task RealApiStopExceptionStillReleasesHelperAndRestores()
    {
        using var sentinel = new Sentinel();
        var original = new IOException("API drain seam");
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults();
        var fixture = OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults);
        var api = Field<ApiServer>(fixture, "Api");
        api.BeforeStopClientDrainWaitForTests = () => throw original;
        Assert.Same(original, Assert.Throws<IOException>(() => fixture.Dispose()));
        AllReleased(faults); sentinel.Restored();
        await ExistingContractAfterFailure(sentinel);
    }

    [Fact]
    public void MultipleCleanupFailuresKeepAllOriginalObjects()
    {
        using var sentinel = new Sentinel();
        var first = new IOException("client cleanup");
        var second = new IOException("API cleanup");
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults {
            AfterRelease = name => { if (name == "client") throw first; if (name == "api") throw second; } };
        var fixture = OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults);
        var failure = Assert.Throws<AggregateException>(() => fixture.Dispose());
        Assert.Equal(new Exception[] { first, second }, failure.InnerExceptions);
        AllReleased(faults); sentinel.Restored(); fixture.Dispose(); AllReleased(faults);
    }

    [Fact]
    public async Task HelperConstructionFailureReclaimsPartialCopyBeforeFixtureRestores()
    {
        using var sentinel = new Sentinel();
        var original = new IOException("partial helper deployment");
        string? privateRoot = null;
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults {
            CreateHelper = root => new FakeAudioHelperDeployment(root, copied => {
                privateRoot = Path.Combine(root, "fake-audio-helper");
                Assert.True(File.Exists(copied));
                throw original;
            }) };
        Assert.Same(original, Assert.Throws<IOException>(() => OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults)));
        Assert.NotNull(privateRoot); Assert.False(Directory.Exists(privateRoot));
        Assert.Empty(faults.Acquired); Assert.Empty(faults.Released);
        sentinel.Restored();
        await ExistingContractAfterFailure(sentinel);
    }

    [Fact]
    public void NoOverrideAndNullEnvironmentAreRestored()
    {
        using var sentinel = new Sentinel();
        DataDirResolver.ClearOverride();
        Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", null);
        try
        {
            var fixture = OrdinaryAvStorageSafetyTests.CreateFixtureForTests();
            fixture.Dispose(); fixture.Dispose();
            Assert.False(DataDirResolver.HasOverride);
            Assert.Null(Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR"));
            Assert.Same(sentinel.Provider, RecordingPreflightChecker.StorageCapacityProvider);
            Assert.Same(sentinel.Resolver, RecordingPreflightChecker.AudioHelperPathResolver);
        }
        finally
        {
            DataDirResolver.SetOverride(sentinel.Root);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", sentinel.EnvironmentValues["AGENT_RECORDER_DATA_DIR"]);
        }
        sentinel.Restored();
    }

    [Fact]
    public void NormalAndRepeatedDisposeRestoreSentinelOnlyOnce()
    {
        using var sentinel = new Sentinel();
        var faults = new OrdinaryAvStorageSafetyTests.FixtureFaults();
        var fixture = OrdinaryAvStorageSafetyTests.CreateFixtureForTests(faults);
        fixture.Dispose(); AllReleased(faults); sentinel.Restored();
        using (var later = OrdinaryAvStorageSafetyTests.CreateFixtureForTests())
        {
            var provider = RecordingPreflightChecker.StorageCapacityProvider;
            var resolver = RecordingPreflightChecker.AudioHelperPathResolver;
            var dir = DataDirResolver.Resolve();
            var environment = Sentinel.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            fixture.Dispose(); AllReleased(faults, expectGlobalReset: false);
            Assert.Same(provider, RecordingPreflightChecker.StorageCapacityProvider);
            Assert.Same(resolver, RecordingPreflightChecker.AudioHelperPathResolver);
            Assert.Equal(dir, DataDirResolver.Resolve());
            foreach (var pair in environment) Assert.Equal(pair.Value, Environment.GetEnvironmentVariable(pair.Key));
        }
        sentinel.Restored();
    }
}
