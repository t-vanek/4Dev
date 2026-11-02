using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TMap.Data;
using TMap.Server.Map;

// These tests deliberately verify the journal plus native quest/item/hotkey SQL stages. They do
// not substitute for a full game-server restart or all SaveChar companion/global-account stages.
internal static class SaveRecoveryCheck
{
    public static async Task RunAsync(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("QUEST_TEST_DB") ?? throw new InvalidOperationException("QUEST_TEST_DB required");
        var builder = new SqlConnectionStringBuilder(connection);
        if (!builder.InitialCatalog.StartsWith("4DevQuestTest_", StringComparison.Ordinal))
            throw new InvalidOperationException("Isolated 4DevQuestTest_* database required");
        var map = new GameDatabase(connection);
        string directory = args[1];
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task Exec(string sql)
        {
            await using var db = new SqlConnection(connection); await db.OpenAsync();
            await using var cmd = new SqlCommand(sql, db); await cmd.ExecuteNonQueryAsync();
        }
        async Task<long> Number(string sql)
        {
            await using var db = new SqlConnection(connection); await db.OpenAsync();
            await using var cmd = new SqlCommand(sql, db); return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        var item = new ItemSaveData(900000001, 0, 255, 2, 200, 7, 3, 4, 500, 450, 6, 0, 8,
            [1, 2, 3, 4, 5, 6], [101, 102, 103, 104, 105, 106], [11, 12, 13, 14, 15, 16], 9, 201);
        var hotkey = new HotkeySaveRow(2, 7, [(1, 600), (2, 201)]);
        async Task Apply(SaveQueueEntry entry)
        {
            if (entry.Character is { } s)
            {
                await map.SaveQuestsAsync(s.Character.CharId, s.Quests, requireProcedures: true);
                // Exit without disposal/acknowledgement after SQL committed only the first stage.
                if (args[0] == "--save-write") Environment.Exit(73);
                foreach (var it in s.Items!) await map.SaveItemDirectAsync(s.Character.CharId, it, requireProcedures: true);
                await map.SaveHotkeysAsync(s.Character.CharId, s.Hotkeys, requireProcedures: true);
            }
            else
            {
                foreach (var it in entry.Upserts!) await map.SaveItemDirectAsync(it.CharId, it.Item, requireProcedures: true);
                foreach (long id in entry.Deletes!) await map.DeleteItemDirectAsync(id);
            }
        }
        if (args[0] is "--save-write" or "--save-recover" or "--save-expect-failure")
        {
            using var journal = new SqlSaveQueue(new CharacterSaveQueueStore(connection, directory),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.InitialCatalog))));
            if (args[0] == "--save-write")
            {
                var data = MapService.BuildCharSave(new Character { CharId = 1, Gold = 20, Exp = 100 });
                journal.Append(SaveQueueEntry.Full(new(data, [new(3000, 700, 1, 1, [new(500, 3, 2)])],
                    [new(255, 0, 0, 0)], [item], [hotkey], [], [], 255, 0, [])));
                journal.Append(SaveQueueEntry.Direct([new(1, item with { Count = 4 })], []));
            }
            if (args[0] == "--save-expect-failure")
            {
                try { await journal.DrainAsync(journal.Boundary, Apply); }
                catch (SqlException) { Require(journal.PendingCount == 2, "Failed record was discarded"); return; }
                throw new InvalidOperationException("Expected injected SQL failure");
            }
            await journal.DrainAsync(journal.Boundary, Apply);
            Require(journal.PendingCount == 0, "Recovery left pending records");
            return;
        }
        async Task RunChild(string mode, int expectedExit = 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(mode); start.ArgumentList.Add(directory);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start journal test process");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            // Do not print exception stacks containing connection configuration.
            Require(child.ExitCode == expectedExit, $"Child {mode} exit {child.ExitCode}, expected {expectedExit}");
            await output; await error;
        }
        Require(args[0] == "--save-check", "Unknown check mode");
        Require(args.Length == 3, "--save-check requires SQL queue scope and migration 043 path");
        await SqlQueueFixture.ApplyMigration(connection, args[2]);
        Require(await SqlQueueFixture.Pending(connection, directory) == 0, "Fresh SQL queue scope required");
        var passed = new List<string>();
        await RunChild("--save-write", 73);
        Require(await Number("SELECT bCompleteCount FROM TQUESTTABLE WHERE dwCharID=1 AND dwQuestID=3000") == 1, "First SQL stage did not commit");
        Require(await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE dlID=900000001") == 0, "Interrupted process wrote later stages");
        Require(await SqlQueueFixture.Pending(connection, directory) == 2, "Abrupt exit lost durable SQL records");
        passed.Add("separate writer exits after quest SQL commit; full snapshot and later item delta survive");
        await Exec("ALTER TABLE TITEMTABLE ADD CONSTRAINT TestJournalItem CHECK (dlID<>900000001)");
        await RunChild("--save-expect-failure");
        Require(await SqlQueueFixture.Pending(connection, directory) == 2, "SQL failure dropped pending records");
        passed.Add("new recovery process retains both records after injected item SQL failure");
        await Exec("ALTER TABLE TITEMTABLE DROP CONSTRAINT TestJournalItem");
        await RunChild("--save-recover");
        Require(await Number("SELECT bCount FROM TITEMTABLE WHERE dlID=900000001") == 4, "Older full snapshot overtook later item delta");
        Require(await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE dlID=900000001 AND bMagic2=2 AND wValue2=102 AND dwTime2=12 AND bGem=9 AND wMoggItemID=201") == 1, "Item attributes lost in recovery");
        Require(await Number("SELECT COUNT(*) FROM THOTKEYTABLE WHERE dwCharID=1 AND bInvenID=7") == 1, "Hotkey page not restored");
        passed.Add("third process replays native quest/item/hotkey SQL; later item state and full attributes win");
        await map.SaveHotkeysAsync(1, [hotkey], requireProcedures: true);
        await map.SaveHotkeysAsync(1, [hotkey], requireProcedures: true);
        Require(await Number("SELECT COUNT(*) FROM THOTKEYTABLE WHERE dwCharID=1 AND bInvenID=7") == 1, "Hotkey replay duplicated inserts");
        var edited = hotkey with { Verb = 3, Slots = [(1, 601)] };
        await map.SaveHotkeysAsync(1, [edited], requireProcedures: true);
        Require((await map.LoadHotkeysAsync(1)).Single(p => p.InvenKey == 7).Slots[0].Id == 601, "Hotkey update lost values");
        await map.SaveHotkeysAsync(1, [hotkey with { Verb = 1 }], requireProcedures: true);
        await map.SaveHotkeysAsync(1, [edited], requireProcedures: true); // repair a missing update row
        Require(await Number("SELECT COUNT(*) FROM THOTKEYTABLE WHERE dwCharID=1 AND bInvenID=7") == 1, "Missing-row update was lost");
        passed.Add("hotkey insert/update/delete replay is idempotent and missing update rows recover");
        await Exec("ALTER TABLE THOTKEYTABLE ADD CONSTRAINT TestJournalHotkey CHECK(bInvenID<>8)");
        try
        {
            await map.SaveHotkeysAsync(1, [hotkey, hotkey with { InvenKey = 8 }], requireProcedures: true);
            throw new InvalidOperationException("Expected hotkey constraint failure");
        }
        catch (SqlException) { }
        Require((await map.LoadHotkeysAsync(1)).Single(p => p.InvenKey == 7).Slots[0].Id == 601, "Failed batch erased previous hotkeys");
        await Exec("ALTER TABLE THOTKEYTABLE DROP CONSTRAINT TestJournalHotkey");
        passed.Add("second hotkey page failure rolls the entire replacement transaction back");
        await RunChild("--save-recover");
        Require(await SqlQueueFixture.Pending(connection, directory) == 0, "Acknowledged SQL queue replayed on next process");
        passed.Add("fourth process observes an empty acknowledged journal");
        // Missing native procedures must never acknowledge a durable write.
        await Exec("EXEC sp_rename 'TSaveQuestTerm', 'TestHiddenSaveQuestTerm'");
        try
        {
            try { await map.SaveQuestsAsync(1, [new(3001, 0, 1, 1, [new(1, 3, 1)])], requireProcedures: true); throw new InvalidOperationException("Missing procedure was acknowledged"); }
            catch (SqlException ex) when (ex.Number == 2812) { }
            Require(await Number("SELECT COUNT(*) FROM TQUESTTABLE WHERE dwQuestID=3001") == 0, "Missing procedure left partial quest");
        }
        finally { await Exec("EXEC sp_rename 'TestHiddenSaveQuestTerm', 'TSaveQuestTerm'"); }
        passed.Add("strict durable quest save rejects missing procedure and rolls its header back");
        Console.WriteLine(JsonSerializer.Serialize(new { database = builder.InitialCatalog, passed,
            scope = "separate journal processes plus native quest/item/hotkey SQL; not a full game-server restart" }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
