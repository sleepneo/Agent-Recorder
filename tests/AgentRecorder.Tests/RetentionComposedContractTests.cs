using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionComposedContractTests
{
    [Fact]
    public async Task ActualEngineAndDatabaseUseSameHeldObjectsAndPrivateSynchronousContext()
    {
        using var f = new RetentionComposedFixture(); var db = new RetentionGuardFixture.TraceDatabase(); using var service = f.Service(db);
        var before = SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, true);
        f.Files.Api.Before = c => { if (c.Stage == "close") { Assert.Equal(db.Begins, db.ConnectionDisposes); Assert.Equal(System.Data.ConnectionState.Closed, db.Connection!.State); } };
        RetentionComposedScope.ScopeContext? saved = null; OriginalFileScopeFixture.Node[]? nodes = null; var calls = 0;
        var session = f.Execute(service, context =>
        {
            context.RequireActive(); saved = context; calls++; nodes = f.Files.Api.Opened.ToArray();
            Assert.Equal(f.Row.RecordingId, context.RecordingId); Assert.Equal(1, db.Begins); Assert.Equal(0, db.Rollbacks);
            Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(db.Connection!.Handle)); f.AssertProtected();
            foreach (var policy in new[] { "fail", "rename" }) Assert.Equal("OUTPUT_PATH_IN_USE", Assert.Throws<ApiException>(() => f.Create(policy)).Code);
            Assert.Empty(f.Engine._recs); Assert.Equal(0, f.Capture.Starts); Assert.Equal(0, f.Ui.Requests);
            Exception? error = null; var thread = new Thread(() => { try { context.RequireActive(); } catch (Exception e) { error = e; } });
            thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(1))); Assert.IsType<InvalidOperationException>(error);
            return RetentionComposedContinuationResult.Completed;
        });
        Assert.Equal(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(session)).Reason);
        var retirement = await session.Retirement; Assert.True(retirement.Preparation!.ReservationReleased); Assert.True(retirement.Database!.Retired);
        Assert.Equal(OriginalFileBorrowReason.Borrowed, retirement.Borrow!.Reason); Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => saved!.RequireActive()); Assert.Equal(nodes, f.Files.Api.Opened.ToArray());
        Assert.All(f.Files.Api.Calls.Where(c => c.Handle is not null), c => Assert.Same(nodes![c.Index].Handle, c.Handle));
        OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
        Assert.Equal(before, SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, true));
        foreach (var type in new[] { typeof(RetentionComposedScope.ScopeContext), typeof(OriginalFileBorrowContext) })
        {
            Assert.All(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), c => Assert.True(c.IsPrivate));
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), m =>
                m.ReturnType == typeof(IntPtr) || typeof(System.Runtime.InteropServices.SafeHandle).IsAssignableFrom(m.ReturnType) ||
                typeof(SqliteConnection).IsAssignableFrom(m.ReturnType) || typeof(Task).IsAssignableFrom(m.ReturnType));
        }
        Assert.Equal(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(f.Execute(service))).Reason);
    }

    [Theory]
    [InlineData("id")][InlineData("nul")][InlineData("cutoff")][InlineData("anchor")][InlineData("future")]
    public async Task InvalidInputNeverPreparesOrOpens(string kind)
    {
        using var f = new RetentionComposedFixture(); var db = new RetentionGuardFixture.TraceDatabase(); using var service = f.Service(db); var calls = 0;
        var session = service.Execute(kind == "id" ? "../candidate" : kind == "nul" ? "candidate\0" : f.Row.RecordingId,
            kind == "anchor" ? f.Anchor with { Length = -1 } : f.Anchor,
            kind == "cutoff" ? f.Cutoff.ToOffset(TimeSpan.FromHours(8)) : kind == "future" ? DateTimeOffset.UtcNow.AddDays(1) : f.Cutoff,
            _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        Assert.Equal(RetentionComposedReason.InvalidInput, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Empty(f.Files.Api.Calls); Assert.Null(db.Connection); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("platform")][InlineData("drive")][InlineData("open")][InlineData("identity")]
    public async Task PreparationFailurePrecedesDatabase(string fault)
    {
        using var f = new RetentionComposedFixture(); var db = new RetentionGuardFixture.TraceDatabase();
        if (fault == "platform") f.Files.Api.Platform = false;
        if (fault == "drive") f.Files.Api.Drive = 4;
        if (fault == "open") f.Files.Api.Fail = c => c.Stage == "open";
        if (fault == "identity") f.Files.Api.IdentityValue = (n, _, v) => { if (n.Leaf) v.IdHigh++; return v; };
        using var service = f.Service(db); var calls = 0;
        var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        Assert.Equal(RetentionComposedReason.PreparationFailed, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Null(db.Connection); Assert.Equal(0, calls); Assert.NotNull((await session.Retirement).PreparationScopeReason);
    }

    [Theory]
    [InlineData("candidate")][InlineData("schema")][InlineData("unknown")][InlineData("bundle")][InlineData("open")][InlineData("begin")]
    public async Task DatabaseRejectsBeforeBorrowAndReleasesPreparedScope(string fault)
    {
        using var f = new RetentionComposedFixture();
        if (fault == "bundle") f.Db.Corrupt("UPDATE ordinary_recording_bundles SET status_code='generating',bundle_path=$path,operation_id=$op;",
            ("$path", OrdinaryRecordingBundleOperation.DerivePath(f.Media)), ("$op", new string('a', 32)));
        if (fault is "candidate" or "schema" or "unknown")
        {
            using var c = Writer(f); RetentionGuardFixture.Write(c, fault switch
            {
                "candidate" => "UPDATE ordinary_recording_history SET status_code='failed';",
                "schema" => "DELETE FROM schema_migrations WHERE version=42;",
                "unknown" => "DROP INDEX idx_ordinary_publication_identity;",
                _ => throw new InvalidOperationException()
            });
        }
        var db = new RetentionGuardFixture.TraceDatabase { Fault = fault is "open" or "begin" ? fault : null };
        using var service = f.Service(db); var calls = 0; var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        Assert.Equal(RetentionComposedReason.DatabaseRejected, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Null((await session.Retirement).Borrow); Assert.Equal(0, calls); OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
    }

    [Fact]
    public async Task ActualCompetingImmediateTransactionRejectsBeforeBorrow()
    {
        using var f = new RetentionComposedFixture(); using var writer = Writer(f); using var transaction = writer.BeginTransaction(deferred: false);
        using var service = f.Service(); var calls = 0; var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        Assert.Equal(RetentionComposedReason.DatabaseRejected, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Equal(RetentionGuardReason.DatabaseBusy, (await session.Retirement).DatabaseReason); Assert.Equal(0, calls);
        transaction.Rollback(); Assert.Equal(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(f.Execute(service))).Reason);
    }

    [Theory]
    [InlineData("identity")][InlineData("length")][InlineData("path")][InlineData("directory")][InlineData("metadata")]
    public async Task DatabasePhaseCannotReplaceTheAlreadyHeldObjects(string fault)
    {
        using var f = new RetentionComposedFixture(); OriginalFileScopeFixture.Node[]? nodes = null;
        var db = new RetentionGuardFixture.TraceDatabase { BeforeOpen = () =>
        {
            nodes = f.Files.Api.Opened.ToArray();
            if (fault == "identity") f.Files.Api.IdentityValue = (n, _, v) => { if (n.Leaf) v.IdHigh++; return v; };
            if (fault == "directory") f.Files.Api.IdentityValue = (n, _, v) => { if (!n.Leaf) v.IdHigh++; return v; };
            if (fault == "length") f.Files.Api.StandardValue = (n, _, v) => { if (n.Leaf) v.EndOfFile++; return v; };
            if (fault == "path") f.Files.Api.PathValue = (n, _, p) => n.Leaf ? p + ".other" : p;
            if (fault == "metadata") f.Files.Api.Fail = c => c.Stage == "basic";
        } };
        using var service = f.Service(db); var calls = 0; var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        Assert.Equal(RetentionComposedReason.BorrowFailed, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Equal(OriginalFileBorrowReason.RecheckFailed, (await session.Retirement).Borrow!.Reason); Assert.Equal(0, calls);
        Assert.Equal(nodes, f.Files.Api.Opened.ToArray());
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CallbackExceptionsAndInvalidEnumsNeverRetry(bool invalidEnum)
    {
        using var f = new RetentionComposedFixture(); using var service = f.Service(); var calls = 0;
        var session = f.Execute(service, _ => { calls++; if (invalidEnum) return (RetentionComposedContinuationResult)99; throw new IOException("PRIVATE_CALLBACK"); });
        Assert.Equal(RetentionComposedReason.ContinuationFailed, (await RetentionComposedFixture.Finish(session)).Reason); Assert.Equal(1, calls);
        Assert.Equal(OriginalFileBorrowReason.CallbackFailed, (await session.Retirement).Borrow!.Reason);
        Assert.Equal(RetentionComposedReason.Checked, (await RetentionComposedFixture.Finish(f.Execute(service))).Reason);
    }

    internal static SqliteConnection Writer(RetentionComposedFixture f)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Db.Store.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1 }.ToString());
        c.Open(); SqliteOperationalStore.ConfigureRetentionGuardConnection(c); return c;
    }

    [Fact]
    public async Task MissingExistingOnlyDatabaseRetiresPreparedObjectsWithoutCreatingDatabaseOrParent()
    {
        using var f = new RetentionComposedFixture(); var missing = Path.Combine(f.Db.Root, "NeverCreated", "missing.db");
        var store = new SqliteOperationalStore(missing); using var writer = new SqliteOrdinaryRecordingHistoryStore(store);
        using var service = f.Service(store: store, writer: writer); var calls = 0;
        var session = f.Execute(service, _ => { calls++; return RetentionComposedContinuationResult.Completed; });
        Assert.Equal(RetentionComposedReason.DatabaseRejected, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Equal(RetentionGuardReason.Unavailable, (await session.Retirement).DatabaseReason); Assert.Equal(0, calls);
        Assert.False(File.Exists(missing)); Assert.False(Directory.Exists(Path.GetDirectoryName(missing))); OriginalFileScopeFixture.AssertClosedInReverse(f.Files.Api);
    }

    [Fact]
    public async Task ActualQueuedWriterFailureIsFlushedBeforeOpenAndDoesNotBorrow()
    {
        using var f = new RetentionComposedFixture(); var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new SqliteOrdinaryRecordingHistoryStore(f.Db.Store, (_, _) => failed.TrySetResult());
        var row = f.Db.Recording("unsettled"); writer.Register(row); writer.BeforePersistForTests = _ => throw new IOException("PRIVATE_QUEUE");
        Assert.True(writer.TryPublish(row with { Status = "failed", ReasonCode = "fixture_failed", CompletedAtUtc = UnifiedHistoryTestDatabase.At.AddSeconds(10) }));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)); var db = new RetentionGuardFixture.TraceDatabase();
        using var service = f.Service(db, writer: writer); var session = f.Execute(service);
        Assert.Equal(RetentionComposedReason.DatabaseRejected, (await RetentionComposedFixture.Finish(session)).Reason);
        Assert.Equal(RetentionGuardReason.Unavailable, (await session.Retirement).DatabaseReason); Assert.Null(db.Connection); Assert.Null((await session.Retirement).Borrow);
    }

    [Fact]
    public async Task UnknownNativeAcquisitionRetainsReservationAndNeverEntersDatabase()
    {
        using var f = new RetentionComposedFixture(); f.Files.Api.NullOpen = 0; var db = new RetentionGuardFixture.TraceDatabase(); using var service = f.Service(db);
        var session = f.Execute(service); Assert.Equal(RetentionComposedReason.Unavailable, (await session.Response.WaitAsync(TimeSpan.FromSeconds(8))).Reason);
        var retirement = await session.Retirement.WaitAsync(TimeSpan.FromSeconds(8)); Assert.False(retirement.Retired);
        Assert.Equal(RetentionComposedReason.PreparationFailed, retirement.Primary); Assert.False(retirement.Preparation!.ReservationReleased); Assert.Null(db.Connection);
        f.AssertProtected(); Assert.Equal(RetentionComposedReason.Busy, (await RetentionComposedFixture.Finish(f.Execute(service))).Reason);
    }
}
