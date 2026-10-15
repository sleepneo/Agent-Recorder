using AgentRecorder.Persistence;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

internal static class DeletionLedgerFixture
{
    internal static DeletionLedger Reader(LocalRetentionDeletionFixture f,string? sid=null,string? binding=null)
        => new(f.Db.Store,sid??UnifiedHistoryTestDatabase.Sid,binding??UnifiedHistoryTestDatabase.Session,f.Videos,f.Clock);
    internal sealed class Io : DeletionLedgerDatabase
    {
        internal Action<string,SqliteConnection,SqliteTransaction?>? OnStep;
        internal string? Fault;
        internal string? DisposeFault;
        internal bool CommitThenThrow;
        internal readonly List<(string Stage,int Thread)> Calls=new();
        internal int Commits;
        internal SqliteConnection? LastConnection;
        internal override void Step(string stage,SqliteConnection c,SqliteTransaction? t)
        {
            Calls.Add((stage,Environment.CurrentManagedThreadId));LastConnection=c;
            if(stage.EndsWith("before_begin"))
            {
                using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous;";Assert.Equal(2L,cmd.ExecuteScalar());
                cmd.CommandText="PRAGMA foreign_keys;";Assert.Equal(1L,cmd.ExecuteScalar());
                cmd.CommandText="PRAGMA busy_timeout;";Assert.Equal(1000L,cmd.ExecuteScalar());Assert.Equal(1,c.DefaultTimeout);
                Assert.Equal(1,SQLitePCL.raw.sqlite3_get_autocommit(c.Handle));
            }
            OnStep?.Invoke(stage,c,t);if(Fault==stage)throw new IOException("OWNED_LEDGER_STAGE_FAULT");
        }
        internal override void Commit(SqliteTransaction t)
        {Commits++;base.Commit(t);if(CommitThenThrow)throw new IOException("COMMIT_RETURN_UNKNOWN");}
        internal override void DisposeTransaction(SqliteTransaction t)
        {if(DisposeFault=="transaction")throw new IOException("OWNED_TXN_DISPOSE");base.DisposeTransaction(t);}
        internal override void DisposeConnection(SqliteConnection c)
        {if(DisposeFault=="connection")throw new IOException("OWNED_CONNECTION_DISPOSE");base.DisposeConnection(c);}
    }
    internal static void CorruptBinding(LocalRetentionDeletionFixture f,string sql)
    {
        using var c=f.Writer();using var cmd=c.CreateCommand();
        cmd.CommandText="DROP TRIGGER deletion_transition; PRAGMA ignore_check_constraints=ON;";cmd.ExecuteNonQuery();
        cmd.CommandText=sql;cmd.ExecuteNonQuery();
        cmd.CommandText=SqliteSchemaV43.Objects.Single(o=>o.Name=="deletion_transition").Sql;cmd.ExecuteNonQuery();
    }
    internal static void Drop43(UnifiedHistoryTestDatabase db)
    {
        db.Ordinary.FlushBeforeUnifiedRead(TimeSpan.FromSeconds(3));
        db.RawExecute("DROP TABLE ordinary_deletion_requests; DELETE FROM schema_migrations WHERE version=44; DROP TABLE ordinary_deletion_operations; DELETE FROM schema_migrations WHERE version=43;");
    }
}
