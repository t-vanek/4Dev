using System.Diagnostics;
using Microsoft.Data.SqlClient;
using TMap.Data;

internal static class QuestAdmissionCheck
{
    public static async Task RunAsync(GameDatabase map, string connection, List<string> passed)
    {
        var settings = new SqlConnectionStringBuilder(connection);
        if (!settings.InitialCatalog.StartsWith("4DevQuestTest_", StringComparison.Ordinal))
            throw new InvalidOperationException("Isolated database required");
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task<long> Number(string sql)
        {
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var cmd = new SqlCommand(sql, c); return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        async Task Exec(string sql)
        {
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync();
        }
        var row = new QuestSaveRow(4000, 700, 0, 1, [new(500, 3, 2)]);
        await map.SaveQuestsAsync(1, [row], requireProcedures: true);
        // Block only the term read. Once the reader waits here, its header SELECT has finished.
        // An old two-autocommit read would release the header and allow a mixed-version update.
        await using var blocker = new SqlConnection(connection); await blocker.OpenAsync();
        int blockerId;
        await using (var spid = new SqlCommand("SELECT @@SPID", blocker)) blockerId = Convert.ToInt32(await spid.ExecuteScalarAsync());
        await using var tx = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var hold = new SqlCommand("UPDATE TQUESTTERMTABLE WITH (ROWLOCK) SET bCount=bCount WHERE dwCharID=1 AND dwQuestID=4000", blocker, tx))
            await hold.ExecuteNonQueryAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reading = map.LoadQuestsAsync(1, deadline.Token);
        try
        {
            var wait = Stopwatch.StartNew();
            bool blocked = false;
            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                blocked = await Number($"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND blocking_session_id={blockerId} AND wait_type LIKE 'LCK%'") != 0;
                if (blocked) break;
                if (reading.IsCompleted) break;
                await Task.Delay(20);
            }
            Require(blocked, "Quest reader did not reach the blocked term SELECT");
            bool protectedHeader = false;
            try { await Exec("SET LOCK_TIMEOUT 500; UPDATE TQUESTTABLE SET bTriggerCount=2 WHERE dwCharID=1 AND dwQuestID=4000"); }
            catch (SqlException ex) when (ex.Number == 1222) { protectedHeader = true; }
            Require(protectedHeader, "Header read locks were released before reading term counters");
        }
        finally { await tx.RollbackAsync(); }
        var loaded = await reading;
        Require(loaded.quests.Single(q => q.QuestId == 4000).TriggerCount == 1 &&
            loaded.terms.Single(t => t.QuestId == 4000).Count == 2, "Quest load mixed transaction state");
        passed.Add("blocked term read retains header locks until the entire quest snapshot is loaded");
        await map.SaveQuestsAsync(1, [row with { TriggerCount = 2, Terms = [new(500, 3, 3)] }], requireProcedures: true);
        loaded = await new GameDatabase(connection).LoadQuestsAsync(1);
        Require(loaded.quests.Single(q => q.QuestId == 4000).TriggerCount == 2 &&
            loaded.terms.Single(t => t.QuestId == 4000).Count == 3, "Writer could not save after the reader committed");
        passed.Add("completed quest read releases locks and a new connection sees the next committed state");

        await Exec("EXEC sp_rename 'TQUESTTERMTABLE', 'TestHiddenQuestTerms'");
        try
        {
            try { await map.LoadQuestsAsync(1); throw new InvalidOperationException("Failed term read returned partial headers"); }
            catch (SqlException ex) when (ex.Number == 208) { }
            // A thrown load must dispose its transaction, including header locks acquired before failure.
            await Exec("SET LOCK_TIMEOUT 500; UPDATE TQUESTTABLE SET dwTick=600 WHERE dwCharID=1 AND dwQuestID=4000");
        }
        finally { await Exec("EXEC sp_rename 'TestHiddenQuestTerms', 'TQUESTTERMTABLE'"); }
        loaded = await map.LoadQuestsAsync(1);
        Require(loaded.quests.Single(q => q.QuestId == 4000).Tick == 600 && loaded.terms.Any(t => t.QuestId == 4000), "Load did not recover after table restoration");
        passed.Add("term-query SQL failure returns no partial log, releases locks and recovers after schema restoration");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await map.LoadQuestsAsync(1, cancelled.Token); throw new InvalidOperationException("Cancelled read succeeded"); }
        catch (OperationCanceledException) { }
        loaded = await map.LoadQuestsAsync(1);
        Require(loaded.quests.Any(q => q.QuestId == 4000), "Cancellation poisoned a later connection");
        passed.Add("cancelled quest read fails without poisoning a later complete read");
    }
}
