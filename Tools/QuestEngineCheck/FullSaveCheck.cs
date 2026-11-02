using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Server;
using TMap.Server.Map;

// ONLY an isolated SQL instance: native SaveChar/SaveDuring address TGLOBAL_GSP directly.
// The fixture exports metadata, defaults, PKs and native procedures, never player rows.
internal static class FullSaveCheck
{
    private static CharacterSaveSnapshot Snapshot(int phase)
    {
        var ch = new Character { CharId = 1, Level = 20, Gold = (uint)(122 + phase), Silver = 5, Cooper = 6,
            Exp = 777, Hp = 90, Mp = 80, SkillPoint = 8, RegionId = 5, MapId = 1, PosX = 2.5f, PosY = 3, PosZ = -4, Country = 1 };
        ch.Persist.StatLevel = 5; ch.Persist.StatPoint = 6; ch.Persist.StatExp = 777;
        var data = MapService.BuildCharSave(ch) with
        {
            PvpUseablePoint = 25, PvpTotalPoint = 50, PvpRecord = Enumerable.Range(1, 12).Select(i => (uint)i).ToArray(),
            MonthPvPoint = 17, MonthWin = 3, MonthLose = 1, MonthSay = "Full fixture",
            Titles = [((ushort)500, true)],
            Maintains = [new(600, 2, 1000, 1, 1, 1, 1, 2)],
            Recalls = [new(8, 200, 0, 1001, 3, 90, 80, 1, -1, 2, 3, 1000, 4)],
            RecallMaintains = [new(8, 601, 2, 900, 1, 1, 1, 1, 2)],
            SaveDuring = true, Premium = new(400, 1, 5000, 2200000020), ExpItem = new(401, 1, 1000, 2200000020)
        };
        return new(data, [new(100, 700, (byte)phase, (byte)phase, [new(500, 3, (byte)(phase + 1))])],
            [new(255, 0, 0, 0)],
            [new(900000001, 0, 255, 2, 200, 7, (byte)(phase + 2), 4, 500, 450, 6, 0, 8,
                [1, 2, 3, 4, 5, 6], [101, 102, 103, 104, 105, 106], [11, 12, 13, 14, 15, 16], 9, 201)],
            [new(2, 7, [(1, 700), (2, 200)])], [new(5, "Full mount", 0, 7)],
            [new(1, 200, 3, "Full companion", (uint)(122 + phase), 15000, 2, 4, [1, 2, 3, 4, 5, 6], 11,
                [(ushort)(100 + phase), 102], [0, 0], 0)], 1, (uint)(986 + phase), [new(700, 2, 123)]);
    }

    public static async Task RunAsync(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FULL_SAVE_DB") ?? throw new InvalidOperationException("FULL_SAVE_DB required");
        var b = new SqlConnectionStringBuilder(connection);
        if (!b.DataSource.StartsWith("4dev-full-save-sql-", StringComparison.Ordinal) || b.InitialCatalog != "TGame_gsp")
            throw new InvalidOperationException("Disposable full-save SQL container required; never use the running game instance");
        async Task<object?> Scalar(string sql)
        {
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var cmd = new SqlCommand(sql, c); return await cmd.ExecuteScalarAsync();
        }
        async Task Exec(string sql)
        {
            await using var c = new SqlConnection(connection); await c.OpenAsync();
            await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync();
        }
        async Task<long> Number(string sql) => Convert.ToInt64(await Scalar(sql));
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        Require(Convert.ToString(await Scalar("SELECT value FROM sys.extended_properties WHERE class=0 AND name=N'4DevDisposableFixture'")) == "full-save-v1",
            "Missing disposable database marker");
        if (args[0].StartsWith("--full-exchange-", StringComparison.Ordinal))
        {
            await ExchangeRecoveryCheck.ChildAsync(args, connection);
            return;
        }
        if (args[0].StartsWith("--full-welcome-", StringComparison.Ordinal))
        {
            await WelcomeMailCheck.ChildAsync(args, connection);
            return;
        }
        if (args[0] == "--full-wolverine-check")
        {
            Require(args.Length == 4, "--full-wolverine-check requires scope, schema output and migration 046 path");
            await WolverineMailCheck.RunAsync(connection, args[2], args[3]);
            return;
        }
        if (args[0] is "--full-wolverine-produce" or "--full-wolverine-crash")
        {
            await WolverineMailCheck.ChildAsync(connection, args[0], Guid.Parse(args[1]));
            return;
        }
        if (args[0].StartsWith("--full-post-", StringComparison.Ordinal))
        {
            await OperatorPostRecoveryCheck.ChildAsync(args, connection);
            return;
        }
        if (args[0].StartsWith("--full-mail-", StringComparison.Ordinal))
        {
            await QuestMailRecoveryCheck.ChildAsync(args, connection);
            return;
        }
        if (args[0].StartsWith("--full-quest-", StringComparison.Ordinal))
        {
            await QuestLifecycleCheck.ChildAsync(args, connection);
            return;
        }
        var map = new GameDatabase(connection);
        var global = new SqlConnectionStringBuilder(connection) { InitialCatalog = "TGlobal_gsp" }.ConnectionString;
        MapService Service()
        {
            var opt = new MapServerOptions { SaveQueueName = args[1] };
            opt.Db.GameConnectionString = connection; opt.Db.GlobalConnectionString = global;
            return new(opt, new MapState(), new Discard(), map, new TemplateStore(), NullLogger<MapService>.Instance);
        }
        if (args[0] is "--full-write" or "--full-fail" or "--full-recover")
        {
            var service = Service(); using var lease = service.OpenSaveQueue();
            if (args[0] != "--full-recover") ((SqlSaveQueue)lease!).Append(SaveQueueEntry.Full(Snapshot(args[0] == "--full-fail" ? 2 : 1)));
            if (args[0] == "--full-fail")
            {
                try { await service.RecoverPendingSavesAsync(); }
                catch (IOException) { Require(((SqlSaveQueue)lease!).PendingCount == 1, "Failed full snapshot was discarded"); return; }
                throw new InvalidOperationException("Expected late companion SQL failure");
            }
            await service.RecoverPendingSavesAsync(); Require(((SqlSaveQueue)lease!).PendingCount == 0, "Unacknowledged full snapshot");
            return;
        }
        Require(args[0] == "--full-check" && args.Length == 3, "--full-check needs a fresh journal root and migration 041 path");
        Require(!string.IsNullOrWhiteSpace(args[1]), "Fresh SQL queue scope required");
        var passed = new List<string>();
        var snapshot = Snapshot(1);
        await map.SaveCharAsync(snapshot.Character, requireProcedures: true);
        var old = await map.LoadCharAsync(1);
        Require(old is { StatLevel: 2, StatPoint: 3, StatExp: 4 }, "Inherited missing-stat defect was not reproduced");
        passed.Add("original native SaveChar ignores requested StatLevel/StatPoint/StatExp (reproduced in disposable SQL)");
        var before = Convert.ToString(await Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID('TSaveChar'))"))!;
        Require(before.Contains("EXEC TEventItemGive") && before.Contains("hugoboss1234"), "Inherited named-gift defect not reproduced");
        passed.Add("original native SaveChar contains a private name-only gift and account-credential lookup");
        async Task Migrate()
        {
            await using var migration = new SqlConnection(connection); await migration.OpenAsync();
            foreach (string batch in Regex.Split(await File.ReadAllTextAsync(args[2]), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch))
                { await using var cmd = new SqlCommand(batch, migration); await cmd.ExecuteNonQueryAsync(); }
        }
        await Migrate(); var once = Convert.ToString(await Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID('TSaveChar'))"));
        await Migrate(); Require(once == Convert.ToString(await Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID('TSaveChar'))")), "Migration 041 is not idempotent");
        passed.Add("migration 041 applies twice without changing the procedure a second time");
        for (int apply = 0; apply < 2; apply++)
        {
            await using var migration = new SqlConnection(connection); await migration.OpenAsync();
            foreach (string batch in Regex.Split(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(args[2])!, "042_quest_post_operations.sql")), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) { await using var cmd = new SqlCommand(batch, migration); await cmd.ExecuteNonQueryAsync(); }
        }
        await SqlQueueFixture.ApplyMigration(connection, Path.Combine(Path.GetDirectoryName(args[2])!, "043_character_save_queue.sql"));
        Require(await SqlQueueFixture.Pending(connection, args[1]) == 0, "Fresh SQL queue scope required");
        await map.SaveCharAsync(snapshot.Character, requireProcedures: true);
        var fixedChar = await new GameDatabase(connection).LoadCharAsync(1);
        Require(fixedChar is { StatLevel: 5, StatPoint: 6, StatExp: 777 }, "Stat values did not survive a fresh connection");
        passed.Add("native SaveChar roundtrips all three previously discarded stat values after repair");
        await Exec("UPDATE TCHARTABLE SET szName='hugoboss1234' WHERE dwCharID=1");
        await map.SaveCharAsync(snapshot.Character, requireProcedures: true); // TEventItemGive deliberately does not exist here.
        Require(!once!.Contains("TEventItemGive") && !once.Contains("szPasswd"), "Private gift/credential branch remains");
        await Exec("UPDATE TCHARTABLE SET szName='FullFixture' WHERE dwCharID=1");
        passed.Add("saving the formerly privileged name succeeds without a gift procedure or credential lookup");
        try { await map.SaveCharAsync(snapshot.Character with { CharId = 999 }, requireProcedures: true); throw new InvalidOperationException("Missing character was acknowledged"); }
        catch (InvalidDataException) { }
        Require(await Number("SELECT COUNT(*) FROM TPVPOINTTABLE WHERE dwCharID=999") == 0, "Rejected character created orphan save data");
        passed.Add("strict native save rejection propagates before auxiliary/orphan records can be written");

        Process Child(string mode)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(mode); start.ArgumentList.Add(args[1]);
            return Process.Start(start) ?? throw new InvalidOperationException("Cannot start full-save process");
        }
        async Task Run(string mode)
        {
            using var child = Child(mode); var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync(); await output; await error;
            Require(child.ExitCode == 0, $"Full-save child {mode} failed ({child.ExitCode}); inspect the isolated fixture");
        }
        await Exec("CREATE TRIGGER FixtureQuestPause ON TQUESTTABLE AFTER INSERT,UPDATE AS BEGIN WAITFOR DELAY '00:00:20'; END");
        using (var writer = Child("--full-write"))
        {
            var stdout = writer.StandardOutput.ReadToEndAsync(); var stderr = writer.StandardError.ReadToEndAsync();
            int waiting = 0; var deadline = Stopwatch.StartNew();
            try
            {
                while (deadline.Elapsed < TimeSpan.FromSeconds(15) && !writer.HasExited)
                {
                    waiting = (int)await Number("SELECT COALESCE(MAX(session_id),0) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND wait_type='WAITFOR'");
                    if (waiting != 0) break;
                    await Task.Delay(50);
                }
                Require(waiting != 0, "Full-save process did not reach the paused quest stage");
            }
            finally
            {
                if (!writer.HasExited) writer.Kill(entireProcessTree: true);
                await writer.WaitForExitAsync(); await stdout; await stderr;
                if (waiting != 0) await Exec($"IF EXISTS(SELECT 1 FROM sys.dm_exec_requests WHERE session_id={waiting} AND database_id=DB_ID() AND wait_type='WAITFOR') EXEC('KILL {waiting}')");
                await Exec("DROP TRIGGER FixtureQuestPause");
            }
        }
        Require(await Number("SELECT COUNT(*) FROM TSKILLTABLE WHERE dwCharID=1 AND wSkillID=700") == 1 &&
            await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE dwOwnerID=1") == 0, "Interrupted save did not stop between committed stages");
        Require(await SqlQueueFixture.Pending(connection, args[1]) == 1, "Killed process lost its durable SQL snapshot");
        passed.Add("killed MapService recovery process leaves earlier SQL stages committed and retains its full durable snapshot");
        await Run("--full-recover"); await Verify(1);
        passed.Add("fresh process replays the real full MapService callback across all character, inventory and global timed-item stages");
        await Exec("ALTER TABLE TCOMPANIONITEMTABLE ADD CONSTRAINT FixtureLateFailure CHECK(wFirstItemID<>102)");
        await Run("--full-fail");
        Require(await Number("SELECT dwGold FROM TCHARTABLE WHERE dwCharID=1") == 124 &&
            await Number("SELECT bCount FROM TITEMTABLE WHERE dlID=900000001") == 4, "Late failure did not exercise the prior committed stages");
        Require(await SqlQueueFixture.Pending(connection, args[1]) == 1, "Late failure discarded the SQL snapshot");
        await Exec("ALTER TABLE TCOMPANIONITEMTABLE DROP CONSTRAINT FixtureLateFailure");
        await Run("--full-recover"); await Verify(2);
        passed.Add("late companion failure retains the snapshot; another process restores every stage without duplicate rewards/items");
        await Run("--full-recover"); await Verify(2);
        Require(await SqlQueueFixture.Pending(connection, args[1]) == 0, "Acknowledged SQL snapshot was replayed");
        passed.Add("another process sees an acknowledged journal and leaves the complete saved state unchanged");
        await QuestLifecycleCheck.RunAsync(connection, args[1] + "-quests", passed);
        await QuestMailRecoveryCheck.RunAsync(connection, args[1] + "-mail", passed);
        await SqlSaveQueueCheck.RunAsync(connection, args[1] + "-queue", passed);
        await TerritoryOccupationCheck.RunAsync(connection, Path.Combine(Path.GetDirectoryName(args[2])!, "044_territory_occupation_targets.sql"), passed);
        await OperatorPostRecoveryCheck.RunAsync(connection, args[1] + "-post", Path.Combine(Path.GetDirectoryName(args[2])!, "045_operator_post_receipts.sql"), passed);
        await ExchangeRecoveryCheck.RunAsync(connection, args[1] + "-exchange", passed);
        await WelcomeMailCheck.RunAsync(connection, args[1] + "-welcome", Path.Combine(Path.GetDirectoryName(args[2])!, "048_character_welcome_mail.sql"), passed);
        Console.WriteLine(JsonSerializer.Serialize(new { passed, scope = "real MapService full journal callback and synthetic quest lifecycle dispatch/recovery in disposable SQL; not a full MapWorker/client playthrough" }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Verify(int phase)
        {
            var ch = await new GameDatabase(connection).LoadCharAsync(1);
            Require(ch is { StatLevel: 5, StatPoint: 6, StatExp: 777, Level: 20, Exp: 777, Hp: 90, Mp: 80, MapId: 1 } && ch.Value.Gold == 122 + phase,
                "Full character header did not roundtrip");
            var items = await map.LoadItemsAsync(1); var it = items.Single();
            Require(it.Count == phase + 2 && it.Magic[1] == 2 && it.Value[1] == 102 && it.Ext[1] == 12 && it.Gem == 9 && it.MoggItemId == 201,
                "Full inventory/item attributes lost or duplicated");
            Require((await map.LoadSkillsAsync(1)).Single() == new SkillLoadRow(700, 2, 123), "Skills lost/duplicated");
            var quests = await map.LoadQuestsAsync(1);
            Require(quests.quests.Single().CompleteCount == phase && quests.terms.Single().Count == phase + 1, "Quest progress lost/duplicated");
            Require((await map.LoadHotkeysAsync(1)).Single().Slots[0].Id == 700, "Hotkey page lost/duplicated");
            Require((await map.LoadTitlesAsync(1)).Single() == ((ushort)500, true), "Title lost/duplicated");
            Require((await map.LoadMaintainAsync(1)).Single().SkillId == 600, "Player buff lost/duplicated");
            var recall = await map.LoadRecallsAsync(1); Require(recall.Recalls.Single().X == -1 && recall.Buffs.Single().Remain == 900, "Recall/buff lost");
            Require((await map.LoadPetsAsync(9)).Single().Name == "Full mount", "Pet ownership lost/duplicated");
            var companion = await map.LoadCompanionsAsync(1);
            Require(companion.Companions.Single().ItemIds[0] == 100 + phase && companion.Medals == 986 + phase && companion.SummonedSlot == 1,
                "Companion equipment/balance lost or duplicated");
            Require(await Number("SELECT COUNT(*) FROM TGLOBAL_GSP.dbo.TDURINGITEMTABLE WHERE dwUserID=9 AND wItemID=400 AND dwRemainTime=5000") == 1 &&
                await Number("SELECT COUNT(*) FROM TEXPITEMTABLE WHERE dwCharID=1 AND wItemID=401 AND dwRemainTime=1000") == 1, "Global premium/EXP lost/duplicated");
            Require(await Number("SELECT COUNT(*) FROM TPVPOINTTABLE WHERE dwCharID=1") == 1 &&
                await Number("SELECT COUNT(*) FROM TMONTHPVPOINTTABLE WHERE dwCharID=1") == 1, "PvP rows lost/duplicated");
        }
    }
    private sealed class Discard : TMap.Server.Net.IWorldSink
    {
        public void Send(byte[] packet) { }
        public void Send(TMap.Protocol.PacketWriter packet) { }
    }
}
