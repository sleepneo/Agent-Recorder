using System.Runtime.InteropServices;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class UnifiedHistoryIndexedSeekTests(ITestOutputHelper output)
{
    internal static void ObserveOwnedPlanAccess(UnifiedHistoryQueryService query, SqliteOperationalStore store, string ownerKind, ITestOutputHelper output)
    {
        query.CandidateCommandForTests = (source, sql, args) =>
        {
            Assert.Equal("plan", source);
            var cost = Measure(store.DatabasePath, sql, args);
            using var connection = store.OpenReadOnlyConnection(); using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var a in args) command.Parameters.AddWithValue(a.Key, a.Value);
            var plans = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) plans.Add(reader.GetString(3));
            Assert.Contains(plans, p => p.Contains("SEARCH plan_history_locator USING INDEX idx_plan_history_identity_created", StringComparison.Ordinal));
            Assert.DoesNotContain(plans, p => p.Contains("TEMP B-TREE", StringComparison.Ordinal));
            Assert.Equal(0, cost.Sorts); Assert.InRange(cost.Rows, 0, 21);
            output.WriteLine($"OWNER_COST kind={ownerKind} rows={cost.Rows} vm_steps={cost.Steps} sorts={cost.Sorts} fullscan={cost.FullScan}");
            output.WriteLine("OWNER_PLAN " + string.Join(" | ", plans));
        };
    }

    [Fact]
    public void ActualCandidateSqlUsesIdentityAndCompleteTimeIdSeeksAt1001000And10000Rows()
    {
        using var d=new UnifiedHistoryTestDatabase(); for(var p=0;p<100;p++) d.SeedPlan("p-"+p);
        var previous=0;
        foreach(var size in new[]{100,1000,10000})
        {
            Populate(d,previous+1,size); previous=size;
            foreach(var source in new[]{"ordinary","plan","future_window"})
            {
                var measured=new List<Cost>();
                d.Query.CandidateCommandForTests=(s,sql,args)=> { var cost=Measure(d.Store.DatabasePath,sql,args); measured.Add(cost); Report(d,s,size,"first-or-next",sql,args,cost); };
                var first=d.Page(20,new(source)); var next=d.Page(20,new(source),first.NextCursor);
                Assert.Equal(20,first.Items.Count); Assert.Equal(20,next.Items.Count);
                Assert.Equal(2,measured.Count); Assert.All(measured,c=> { Assert.Equal(21,c.Rows); Assert.Equal(0,c.Sorts); Assert.InRange(c.Steps,1,10000); });
                Assert.DoesNotContain(first.Items.Select(i=>i.EntryId),id=>next.Items.Any(i=>i.EntryId==id));
            }
        }
    }

    [Fact]
    public void SameTimeDeepPagesAcrossManyPlansUseIndexedSeekAndBoundedValidation()
    {
        using var d=new UnifiedHistoryTestDatabase(); for(var p=0;p<100;p++) d.SeedPlan("p-"+p); Populate(d,1,10000);
        foreach(var source in new[]{"ordinary","plan","future_window"})
        {
            var seen=new HashSet<string>(); var filters=new UnifiedHistoryFilters(source); var page=d.Page(100,filters); var index=0;
            while(true)
            {
                foreach(var item in page.Items) Assert.True(seen.Add(item.EntryId));
                if(page.NextCursor is null) break;
                index++;
                if(index>=98)
                {
                    d.Query.CandidateCommandForTests=(s,sql,args)=> {var cost=Measure(d.Store.DatabasePath,sql,args); Report(d,s,10000,"deep-"+index,sql,args,cost); Assert.Equal(0,cost.Sorts); Assert.InRange(cost.Steps,1,40000); Assert.InRange(cost.Rows,1,101);};
                    d.Query.CandidatesSelectedForTests=(_,count)=>Assert.InRange(count,0,101);
                    d.Query.EntriesValidatedForTests=(_,count)=>Assert.InRange(count,1,100);
                }
                page=d.Page(100,filters,page.NextCursor);
            }
            Assert.Equal(10000,seen.Count); d.Query.CandidateCommandForTests=null;
        }
    }

    [Fact]
    public void ForeignHistoryIsExcludedByIdentitySeekAndWatermarkResidualCostsAreMeasuredHonestly()
    {
        using var d=new UnifiedHistoryTestDatabase(); for(var p=0;p<100;p++) d.SeedPlan("p-"+p); Populate(d,1,100);
        foreach(var p in new[]{"foreign-a","foreign-b"})d.SeedPlan(p,"foreign-sid","foreign-session");
        using(var c=d.Store.OpenConnection()) using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000)
                INSERT INTO plan_occurrences(id,plan_id,status_code,window_start_utc,window_end_utc,created_at_utc,updated_at_utc,version)
                SELECT 'foreign-'||printf('%08d',x), CASE WHEN x%2=0 THEN 'foreign-a' ELSE 'foreign-b' END,'scheduled',$at,$at+600000000,$at+(x%3),$at+(x%3),0 FROM n;
                """; cmd.Parameters.AddWithValue("$at",At.UtcTicks); cmd.ExecuteNonQuery();
        }
        d.Query.CandidateCommandForTests=(s,sql,args)=> {var cost=Measure(d.Store.DatabasePath,sql,args); Report(d,s,100,"owned100-foreign10000",sql,args,cost); Assert.Equal(0,cost.Sorts); Assert.InRange(cost.Steps,1,10000);};
        var first=d.Page(20,new("plan")); var next=d.Page(20,new("plan"),first.NextCursor); Assert.Equal(20,next.Items.Count);
        d.Query.CandidateCommandForTests=null;
        // Freeze the future source at 100 original members, then insert 1,000
        // old and 1,000 new dates. These remain outside its insertion watermark.
        var filters=new UnifiedHistoryFilters("future_window"); var page=d.Page(20,filters); var ids=page.Items.Select(i=>i.EntryId).ToList();
        PopulateFutureLate(d,1000,At.AddDays(-1),"late-old"); PopulateFutureLate(d,1000,At.AddDays(1),"late-new");
        d.Query.CandidateCommandForTests=(s,sql,args)=> {var cost=Measure(d.Store.DatabasePath,sql,args); Report(d,s,100,"after-late-inserts",sql,args,cost); Assert.Equal(0,cost.Sorts);};
        while(page.NextCursor is not null) {page=d.Page(20,filters,page.NextCursor); ids.AddRange(page.Items.Select(i=>i.EntryId));}
        Assert.Equal(100,ids.Count); Assert.Equal(100,ids.Distinct().Count()); Assert.DoesNotContain(ids,id=>id.Contains("late-"));
    }

    [Fact]
    public void SparseStatusActualSqlUsesCompositeIndexForFirstSameTimeAndEarlierSeeks()
    {
        using var d = new UnifiedHistoryTestDatabase();
        using (var c = d.Store.OpenConnection()) using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000)
                INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,requested_duration_seconds,audio_mode,output_path,updated_at_utc)
                SELECT 'r_'||printf('%08d',x),x,$at-(x/10000),CASE WHEN x%100=0 THEN 'failed' ELSE 'preparing' END,'display','display_capture','Sparse fixture',15,'none',$directory||char(92)||x||'.mp4',$at FROM n;
                """;
            cmd.Parameters.AddWithValue("$at", At.UtcTicks); cmd.Parameters.AddWithValue("$directory", d.Root); cmd.ExecuteNonQuery();
        }
        var calls = 0;
        d.Query.CandidateCommandForTests = (source, sql, args) =>
        {
            calls++; Assert.Equal("ordinary", source); Assert.Contains("status_code=$status", sql);
            Assert.DoesNotContain("OFFSET", sql); Assert.Contains(args, a => a.Key == "$status" && Equals(a.Value, "failed"));
            var cost = Measure(d.Store.DatabasePath, sql, args);
            using var c = d.Store.OpenReadOnlyConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var a in args) cmd.Parameters.AddWithValue(a.Key, a.Value);
            var plans = new List<string>(); using (var r = cmd.ExecuteReader()) while(r.Read()) plans.Add(r.GetString(3));
            Assert.Contains(plans, p => p.Contains("idx_ordinary_recording_history_status_created", StringComparison.Ordinal));
            Assert.DoesNotContain(plans, p => p.Contains("TEMP B-TREE", StringComparison.Ordinal));
            Assert.Equal(0, cost.Sorts); Assert.Equal(0, cost.FullScan); Assert.InRange(cost.Rows, 1, 21); Assert.InRange(cost.Steps, 1, 2000);
            output.WriteLine($"STATUS_COST page={calls} rows={cost.Rows} vm_steps={cost.Steps} sorts={cost.Sorts} fullscan={cost.FullScan}");
            output.WriteLine("STATUS_PLAN " + string.Join(" | ", plans));
        };
        d.Query.CandidatesSelectedForTests = (_, n) => Assert.InRange(n, 0, 21);
        d.Query.EntriesValidatedForTests = (_, n) => Assert.InRange(n, 1, 20);
        var seen = new HashSet<string>(); string? cursor = null;
        do { var page = d.Page(20, new("ordinary", Status: "failed"), cursor); foreach(var item in page.Items) { Assert.Equal("failed",item.Status); Assert.True(seen.Add(item.EntryId)); } cursor=page.NextCursor; } while(cursor is not null);
        Assert.Equal(200, seen.Count); Assert.Equal(10, calls);
    }

    private void Report(UnifiedHistoryTestDatabase d,string source,int size,string position,string sql,IReadOnlyList<KeyValuePair<string,object>> args,Cost cost)
    {
        using var c=d.Store.OpenConnection(); using var cmd=c.CreateCommand(); cmd.CommandText="EXPLAIN QUERY PLAN "+sql; foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value);
        var plans=new List<string>(); using(var r=cmd.ExecuteReader())while(r.Read())plans.Add(r.GetString(3));
        Assert.DoesNotContain(plans,p=>p.Contains("TEMP B-TREE",StringComparison.Ordinal));
        output.WriteLine($"COST source={source} owned={size} position={position} rows={cost.Rows} vm_steps={cost.Steps} sorts={cost.Sorts} fullscan={cost.FullScan}");
        output.WriteLine("PLAN "+string.Join(" | ",plans));
    }

    private static void Populate(UnifiedHistoryTestDatabase d,int first,int last)
    {
        using var c=d.Store.OpenConnection(); using var t=c.BeginTransaction(); using var cmd=c.CreateCommand(); cmd.Transaction=t;
        cmd.CommandText="""
            WITH RECURSIVE n(x) AS(SELECT $first UNION ALL SELECT x+1 FROM n WHERE x<$last)
            INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,requested_duration_seconds,audio_mode,output_path,updated_at_utc)
            SELECT 'r_'||printf('%08d',x),x,$ticks,'preparing','display','display_capture','Scale fixture',15,'none',$directory||char(92)||'r_'||x||'.mp4',$ticks FROM n;
            WITH RECURSIVE n(x) AS(SELECT $first UNION ALL SELECT x+1 FROM n WHERE x<$last)
            INSERT INTO plan_occurrences(id,plan_id,status_code,window_start_utc,window_end_utc,created_at_utc,updated_at_utc,version)
            SELECT 'o_'||printf('%08d',x),'p-'||(x%100),'scheduled',$ticks,$ticks+600000000,$ticks,$ticks,0 FROM n;
            WITH RECURSIVE n(x) AS(SELECT $first UNION ALL SELECT x+1 FROM n WHERE x<$last)
            INSERT INTO future_window_authorizations(authorization_id,idempotency_key,request_digest,current_user_sid,session_binding,executable_path,executable_file_identity,executable_sha256,maximum_duration_seconds,validity_seconds,output_directory,output_file_name,status_code,created_at_utc,updated_at_utc,version)
            SELECT 'f_'||printf('%08d',x),'key-f_'||x,$digest,$sid,$session,'C:\Nonexistent\Player.exe','fixture-identity',$digest,60,300,$directory,'f_'||x||'.mp4','pending',$ms,$ms,0 FROM n;
            """;
        cmd.Parameters.AddWithValue("$first",first);cmd.Parameters.AddWithValue("$last",last);cmd.Parameters.AddWithValue("$ticks",At.UtcTicks);cmd.Parameters.AddWithValue("$ms",At.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$directory",d.Root);cmd.Parameters.AddWithValue("$digest",new string('a',64));cmd.Parameters.AddWithValue("$sid",Sid);cmd.Parameters.AddWithValue("$session",Session);cmd.ExecuteNonQuery();t.Commit();
    }
    private static void PopulateFutureLate(UnifiedHistoryTestDatabase d,int count,DateTimeOffset time,string prefix)
    {
        using var c=d.Store.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText="""
            WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$count)
            INSERT INTO future_window_authorizations(authorization_id,idempotency_key,request_digest,current_user_sid,session_binding,executable_path,executable_file_identity,executable_sha256,maximum_duration_seconds,validity_seconds,output_directory,output_file_name,status_code,created_at_utc,updated_at_utc,version)
            SELECT $prefix||x,$prefix||x,$digest,$sid,$session,'C:\Nonexistent\Player.exe','fixture-identity',$digest,60,300,$directory,$prefix||x||'.mp4','pending',$ms,$ms,0 FROM n;
            """;
        cmd.Parameters.AddWithValue("$count",count);cmd.Parameters.AddWithValue("$prefix",prefix);cmd.Parameters.AddWithValue("$digest",new string('a',64));cmd.Parameters.AddWithValue("$sid",Sid);cmd.Parameters.AddWithValue("$session",Session);cmd.Parameters.AddWithValue("$directory",d.Root);cmd.Parameters.AddWithValue("$ms",time.ToUnixTimeMilliseconds());cmd.ExecuteNonQuery();
    }
    private sealed record Cost(int Rows,int Steps,int Sorts,int FullScan);
    private static Cost Measure(string path,string sql,IReadOnlyList<KeyValuePair<string,object>> args)
    {
        Assert.Equal(0,Native.sqlite3_open_v2(path,out var db,1,IntPtr.Zero));
        try
        {
            Assert.Equal(1,Native.sqlite3_db_readonly(db,"main"));Assert.Equal(0,Native.sqlite3_prepare_v2(db,sql,-1,out var stmt,IntPtr.Zero));
            try
            {
                foreach(var a in args) {var i=Native.sqlite3_bind_parameter_index(stmt,a.Key);Assert.True(i>0);Assert.Equal(0,a.Value is string s?Native.sqlite3_bind_text(stmt,i,s,-1,new IntPtr(-1)):Native.sqlite3_bind_int64(stmt,i,Convert.ToInt64(a.Value)));}
                var rows=0;int rc;while((rc=Native.sqlite3_step(stmt))==100)rows++;Assert.Equal(101,rc);Assert.Equal(0,Native.sqlite3_total_changes(db));
                return new(rows,Native.sqlite3_stmt_status(stmt,4,0),Native.sqlite3_stmt_status(stmt,2,0),Native.sqlite3_stmt_status(stmt,1,0));
            }
            finally {Native.sqlite3_finalize(stmt);}
        }
        finally {Native.sqlite3_close(db);}
    }
    private static class Native
    {
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string file,out IntPtr db,int flags,IntPtr vfs);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_prepare_v2(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int n,out IntPtr stmt,IntPtr tail);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_parameter_index(IntPtr stmt,[MarshalAs(UnmanagedType.LPUTF8Str)]string name);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_text(IntPtr stmt,int i,[MarshalAs(UnmanagedType.LPUTF8Str)]string text,int n,IntPtr destructor);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_int64(IntPtr stmt,int i,long value);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_stmt_status(IntPtr stmt,int op,int reset);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_db_readonly(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)]string name);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_total_changes(IntPtr db);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("e_sqlite3",CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_close(IntPtr db);
    }
}
