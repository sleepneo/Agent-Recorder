using System.Diagnostics;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for the deferred-capture-start capability of
/// <see cref="WgcContinuousCaptureBackend"/> (<see cref="IDeferredCaptureStartBackend"/>).
/// A deferred Start must prepare the helper session without authorizing capture;
/// the explicit StartCapture operation authorizes exactly once. All fakes, no
/// real helper, no real WGC capture, no GUI.
/// </summary>
public sealed class WgcContinuousDeferredStartBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _finalDir;
    private readonly List<IDisposable> _disposables = new();

    public WgcContinuousDeferredStartBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", $"wgc-deferred-{Guid.NewGuid():N}");
        _finalDir = Path.Combine(_tempDir, "final");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_finalDir);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch { /* best effort */ }
        }

        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
                if (!Directory.Exists(_tempDir))
                    break;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    private CaptureConfig CreateValidConfig(bool deferCaptureStart)
    {
        return new CaptureConfig
        {
            SourceKind = "display",
            Bounds = (-100, 0, 1920, 1080),
            DurationSeconds = 5,
            Fps = 30,
            OutputPath = Path.Combine(_finalDir, "out.mp4"),
            DeferCaptureStart = deferCaptureStart
        };
    }

    private WgcContinuousCaptureBackend CreateBackend(DeferredFakeSession session)
    {
        var backend = new WgcContinuousCaptureBackend(
            options =>
            {
                File.WriteAllText(options.HelperExePath, "fake");
                return session;
            },
            new NoOpPublisher(),
            path => new OutputMeta { Container = "mp4", Codec = "h264" },
            () => Path.Combine(_tempDir, "fake-helper.exe"),
            _tempDir);
        _disposables.Add(backend);
        return backend;
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, string? message = null)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.Elapsed < timeout)
            await Task.Delay(10);

        if (!predicate())
            throw new TimeoutException(message ?? "Condition was not met within the allotted timeout.");
    }

    [Fact]
    public async Task Start_WithDeferCaptureStart_PreparesWithoutAuthorizing()
    {
        var session = new DeferredFakeSession();
        var backend = CreateBackend(session);

        backend.Start(CreateValidConfig(deferCaptureStart: true));

        Assert.Equal(1, session.StartCallCount);
        Assert.Equal(0, session.AuthorizeCallCount);
        Assert.True(backend.IsAwaitingCaptureStart);

        // Give any erroneous background authorization a chance to run.
        await Task.Delay(100);
        Assert.Equal(0, session.AuthorizeCallCount);
    }

    [Fact]
    public async Task StartCapture_AuthorizesExactlyOnce_AndReportsSuccess()
    {
        var session = new DeferredFakeSession();
        var backend = CreateBackend(session);
        var notifications = new List<bool>();
        backend.CaptureAuthorizationCompleted += ok => { lock (notifications) notifications.Add(ok); };

        backend.Start(CreateValidConfig(deferCaptureStart: true));
        backend.StartCapture();

        Assert.False(backend.IsAwaitingCaptureStart);
        await WaitForConditionAsync(() => session.AuthorizeCallCount == 1, TimeSpan.FromSeconds(2));
        session.AuthorizeTcs.TrySetResult(true);

        await WaitForConditionAsync(() => { lock (notifications) return notifications.Count == 1; },
            TimeSpan.FromSeconds(2), "authorization completion notification");
        lock (notifications) Assert.True(notifications[0]);
        Assert.Equal(1, session.AuthorizeCallCount);
    }

    [Fact]
    public async Task StartCapture_CalledRepeatedly_StillAuthorizesOnlyOnce()
    {
        var session = new DeferredFakeSession();
        var backend = CreateBackend(session);
        int notificationCount = 0;
        backend.CaptureAuthorizationCompleted += _ => Interlocked.Increment(ref notificationCount);

        backend.Start(CreateValidConfig(deferCaptureStart: true));
        backend.StartCapture();
        backend.StartCapture();
        backend.StartCapture();

        await WaitForConditionAsync(() => session.AuthorizeCallCount == 1, TimeSpan.FromSeconds(2));
        session.AuthorizeTcs.TrySetResult(true);
        await WaitForConditionAsync(() => notificationCount == 1, TimeSpan.FromSeconds(2));

        // Later calls are no-ops even after the first authorization completed.
        backend.StartCapture();
        await Task.Delay(100);
        Assert.Equal(1, session.AuthorizeCallCount);
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task StartCapture_AuthorizationFailure_ReportsFalse()
    {
        var session = new DeferredFakeSession();
        var backend = CreateBackend(session);
        var notifications = new List<bool>();
        backend.CaptureAuthorizationCompleted += ok => { lock (notifications) notifications.Add(ok); };

        backend.Start(CreateValidConfig(deferCaptureStart: true));
        backend.StartCapture();
        await WaitForConditionAsync(() => session.AuthorizeCallCount == 1, TimeSpan.FromSeconds(2));
        session.AuthorizeTcs.TrySetResult(false);

        await WaitForConditionAsync(() => { lock (notifications) return notifications.Count == 1; },
            TimeSpan.FromSeconds(2));
        lock (notifications) Assert.False(notifications[0]);
    }

    [Fact]
    public async Task StartCapture_AuthorizationThrows_ReportsFalse()
    {
        var session = new DeferredFakeSession { ThrowOnAuthorize = true };
        var backend = CreateBackend(session);
        var notifications = new List<bool>();
        backend.CaptureAuthorizationCompleted += ok => { lock (notifications) notifications.Add(ok); };

        backend.Start(CreateValidConfig(deferCaptureStart: true));
        backend.StartCapture();

        await WaitForConditionAsync(() => { lock (notifications) return notifications.Count == 1; },
            TimeSpan.FromSeconds(2));
        lock (notifications) Assert.False(notifications[0]);
    }

    [Fact]
    public void StartCapture_BeforeStart_DoesNotCrash_AndReportsFalse()
    {
        var session = new DeferredFakeSession();
        var backend = CreateBackend(session);
        var notifications = new List<bool>();
        backend.CaptureAuthorizationCompleted += ok => { lock (notifications) notifications.Add(ok); };

        backend.StartCapture();

        Assert.Equal(0, session.AuthorizeCallCount);
        Assert.False(backend.IsAwaitingCaptureStart);
        lock (notifications)
        {
            Assert.Single(notifications);
            Assert.False(notifications[0]);
        }
    }

    [Fact]
    public async Task Start_WithoutDefer_KeepsImmediateAuthorizationBehavior()
    {
        var session = new DeferredFakeSession();
        var backend = CreateBackend(session);

        backend.Start(CreateValidConfig(deferCaptureStart: false));

        // The non-deferred path must authorize exactly as before: promptly,
        // without any StartCapture call, and IsAwaitingCaptureStart stays false.
        await WaitForConditionAsync(() => session.AuthorizeCallCount == 1, TimeSpan.FromSeconds(2));
        Assert.False(backend.IsAwaitingCaptureStart);
    }

    [Fact]
    public async Task Dispose_WhileAwaitingAuthorization_PreventsLaterAuthorization()
    {
        var session = new DeferredFakeSession();
        var backend = CreateBackend(session);
        var notifications = new List<bool>();
        backend.CaptureAuthorizationCompleted += ok => { lock (notifications) notifications.Add(ok); };

        backend.Start(CreateValidConfig(deferCaptureStart: true));
        Assert.True(backend.IsAwaitingCaptureStart);

        backend.Dispose();
        Assert.False(backend.IsAwaitingCaptureStart);

        backend.StartCapture();
        await Task.Delay(100);

        Assert.Equal(0, session.AuthorizeCallCount);
        lock (notifications)
        {
            Assert.Single(notifications);
            Assert.False(notifications[0]);
        }
    }

    // -----------------------------------------------------------------
    // Supporting fakes
    // -----------------------------------------------------------------

    private sealed class DeferredFakeSession : IWgcContinuousBackendSession
    {
        private readonly TaskCompletionSource<bool> _authorizeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WgcContinuousSessionResult> _completionTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartCallCount { get; private set; }
        public int AuthorizeCallCount { get; private set; }
        public bool ThrowOnAuthorize { get; set; }
        public TaskCompletionSource<bool> AuthorizeTcs => _authorizeTcs;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            return Task.CompletedTask;
        }

        public Task<bool> AuthorizeCapture(CancellationToken cancellationToken = default)
        {
            AuthorizeCallCount++;
            if (ThrowOnAuthorize)
                return Task.FromException<bool>(new InvalidOperationException("authorization blew up"));
            return _authorizeTcs.Task;
        }

        public Task<bool> RequestStop(CancellationToken cancellationToken = default)
        {
            _completionTcs.TrySetResult(new WgcContinuousSessionResult
            {
                State = WgcContinuousManagedSessionState.Stopped,
                StopRequestedByCaller = true,
                Summary = new WgcContinuousSessionSummary { State = ContinuousSessionState.Stopped }
            });
            return Task.FromResult(true);
        }

        public Task<WgcContinuousSessionResult> CompletionTask => _completionTcs.Task;

        public event Action<FirstFrameObservation>? FirstFrameObserved;

        public void FireFirstFrame(FirstFrameObservation observation) => FirstFrameObserved?.Invoke(observation);

        public void Dispose()
        {
            _completionTcs.TrySetResult(new WgcContinuousSessionResult
            {
                State = WgcContinuousManagedSessionState.Cancelled,
                FailureCategory = "disposed"
            });
        }
    }

    private sealed class NoOpPublisher : IStagingToFinalPublisher
    {
        public Task<PublishResult> PublishAsync(
            string stagingPath,
            string finalPath,
            CancellationToken cancellationToken = default,
            IFileCommitGate? commitGate = null)
            => Task.FromResult(new PublishResult { Success = false, FailureCategory = "test_publisher" });
    }
}
