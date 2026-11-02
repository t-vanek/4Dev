using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TMap.Data;
using TMap.Server.Map;

internal static class SqlSaveQueueCheck
{
    public static async Task RunAsync(string connection, string scope, List<string> passed)
    {
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task Exec(string sql, string? owner = null)
        {
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var cmd = new SqlCommand(sql, c);
            if (owner is not null) cmd.Parameters.AddWithValue("@owner", owner);
            await cmd.ExecuteNonQueryAsync();
        }
        SqlSaveQueue Open(string owner, string identity = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
            => new(new CharacterSaveQueueStore(connection, owner), identity);
        var item = new ItemSaveData(900000099, 0, 255, 0, 200, 0, 3, 0, 0, 0, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0);
        var entry = SaveQueueEntry.Direct([new(1, item)], []);
        using (var first = Open(scope))
        {
            first.Append(entry);
            bool blocked = false;
            try { using var competing = Open(scope); } catch (SqlException) { blocked = true; }
            Require(blocked, "Two Map writers acquired the same SQL queue lease");
        }
        using (var reopened = Open(scope))
        {
            Require(reopened.PendingCount == 1 && reopened.ReadPending().Single().Upserts!.Single().Item.Count == 3,
                "SQL-only snapshot did not survive closing all queue objects");
            await reopened.DrainAsync(reopened.Boundary, _ => Task.CompletedTask);
            Require(reopened.PendingCount == 0, "SQL acknowledgement did not remove the record");
        }
        passed.Add("SQL queue survives closing/reopening, excludes a competing writer and releases its lease without any filesystem storage");

        string corruptScope = scope + "-corrupt";
        using (var queue = Open(corruptScope)) { queue.Append(entry); queue.Append(entry); }
        bool wrongIdentity = false;
        try { using var wrong = Open(corruptScope, new string('B', 64)); } catch (SqlException) { wrongIdentity = true; }
        Require(wrongIdentity, "SQL queue accepted a different database identity");
        await Exec("UPDATE dbo.TCharacterSaveQueue SET Checksum=REPLICATE('0',64) WHERE OwnerKey=@owner AND Sequence=(SELECT MAX(Sequence) FROM dbo.TCharacterSaveQueue WHERE OwnerKey=@owner)", corruptScope);
        bool rejected = false;
        try { using var queue = Open(corruptScope); } catch (InvalidDataException) { rejected = true; }
        Require(rejected && await SqlQueueFixture.Pending(connection, corruptScope) == 2,
            "Corruption silently discarded or partially applied SQL snapshots");
        await Exec("DELETE dbo.TCharacterSaveQueue WHERE OwnerKey=@owner", corruptScope); // synthetic corrupt fixture only
        passed.Add("SQL identity mismatch and corrupt later payload reject recovery while retaining both records");

        using (var store = new CharacterSaveQueueStore(connection, scope + "-id"))
        {
            store.Open(new string('A', 64)); var id = Guid.NewGuid();
            string payload = JsonSerializer.Serialize(entry, SqlSaveQueue.Json);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            long seq = store.Append(id, hash, payload);
            Require(store.Append(id, hash, payload) == seq && store.Count == 1, "Pending SQL enqueue identity replayed");
            bool mismatch = false;
            try { store.Append(id, new string('B', 64), payload + " "); } catch (SqlException) { mismatch = true; }
            Require(mismatch && store.Count == 1, "Changed SQL queue payload reused a pending identity");
            store.Acknowledge(seq);
        }
        passed.Add("SQL enqueue retries reuse one pending row; mismatched data rolls back without losing the SQL lease");

        string lostScope = scope + "-lost";
        using (var queue = Open(lostScope))
        {
            queue.Append(entry);
            string app = "4dev-character-save:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lostScope)));
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var find = new SqlCommand("SELECT session_id FROM sys.dm_exec_sessions WHERE program_name=@app AND database_id=DB_ID()", c);
            find.Parameters.AddWithValue("@app", app); int spid = Convert.ToInt32(await find.ExecuteScalarAsync());
            Require(spid > 0, "SQL lease session not found");
            await using var kill = new SqlCommand($"IF EXISTS(SELECT 1 FROM sys.dm_exec_sessions WHERE session_id={spid} AND program_name=@app) EXEC('KILL {spid}')", c);
            kill.Parameters.AddWithValue("@app", app); await kill.ExecuteNonQueryAsync();
            bool failed = false;
            try { queue.Append(entry); } catch (Exception ex) when (ex is IOException or SqlException or InvalidOperationException) { failed = true; }
            Require(failed && await SqlQueueFixture.Pending(connection, lostScope) == 1, "Lost SQL lease reconnected or appended another snapshot");
        }
        using (var queue = Open(lostScope)) { await queue.DrainAsync(queue.Boundary, _ => Task.CompletedTask); }
        passed.Add("killed SQL lease session prevents enqueue/reconnect; a new owner recovers the retained SQL row");

        await Exec("EXEC sp_rename 'dbo.TCharacterSaveQueue','FixtureHiddenCharacterSaveQueue'");
        try
        {
            bool missing = false;
            try { using var queue = Open(scope + "-missing"); } catch (SqlException) { missing = true; }
            Require(missing, "Missing SQL queue schema fell back to empty or local storage");
        }
        finally { await Exec("EXEC sp_rename 'dbo.FixtureHiddenCharacterSaveQueue','TCharacterSaveQueue'"); }
        passed.Add("missing required SQL queue table fails startup; migration 043 applies twice; no file fallback exists");
    }
}
