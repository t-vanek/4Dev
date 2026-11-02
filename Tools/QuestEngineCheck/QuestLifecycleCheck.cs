using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

// FULL_SAVE_DB and the disposable marker are validated by FullSaveCheck before entering this driver.
internal static class QuestLifecycleCheck
{
    public static async Task ChildAsync(string[] args, string connection)
    {
        var templates = new TemplateStore();
        foreach (ushort id in new ushort[] { 200, 201 }) templates.Items[id] = new(id, 0, new float[4], Stack: 10, ClassId: uint.MaxValue);
        templates.Skills[300] = new(300, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, 1, 0, ClassId: uint.MaxValue);
        templates.LevelExp[20] = 1000000;
        QuestTemplate Quest(uint id, QuestType type, uint parent = 0, byte trigger = 3, uint key = 500)
        {
            var q = new QuestTemplate { QuestId = id, Type = (byte)type, TriggerType = trigger, TriggerId = key, ParentId = parent, CountMax = 1 };
            templates.Quests[id] = q; return q;
        }
        var mission = Quest(1000, QuestType.Mission);
        mission.Terms.Add(new(200, 2, 1)); mission.Terms.Add(new(200, 5, 1)); mission.Terms.Add(new(500, 3, 1));
        mission.Rewards.Add(new(201, 2, 2, 0, 2)); mission.Rewards.Add(new(37, 1, 1, 0, 0)); mission.Rewards.Add(new(300, 3, 1, 0, 0));
        mission.Rewards.Add(new(150, 10, 1, 0, 0));
        Quest(1001, QuestType.Complete, 1000).Terms.Add(new(1000, 1, 0));
        Quest(1002, QuestType.Mission, 1000, 1, 1001).Terms.Add(new(501, 3, 1));
        Quest(1100, QuestType.Mission).Terms.Add(new(100, 6, 0));
        Quest(1101, QuestType.Complete, 1100).Terms.Add(new(1100, 1, 0));
        var opt = new MapServerOptions { SaveQueueName = args[1] };
        opt.Db.GameConnectionString = connection;
        opt.Db.GlobalConnectionString = new SqlConnectionStringBuilder(connection) { InitialCatalog = "TGlobal_gsp" }.ConnectionString;
        var map = new GameDatabase(connection); var state = new MapState(); var channel = new Channel();
        var service = new MapService(opt, state, new World(), map, templates, NullLogger<MapService>.Instance);
        service.InitQuests(); service.ApplyItemIdSeed(2000000000);
        using var journal = service.OpenSaveQueue();
        await service.RecoverPendingSavesAsync();
        if (args[0] == "--full-quest-recover") return;
        var row = await map.LoadCharAsync(2) ?? throw new InvalidOperationException("Synthetic quest character missing");
        var ch = new Character { CharId = 2, DbLoaded = true, Name = row.Name, Class = row.Class, Country = row.Country, Level = row.Level,
            Gold = row.Gold, Silver = row.Silver, Cooper = row.Cooper, Exp = row.Exp, Hp = row.Hp, Mp = row.Mp, SkillPoint = row.SkillPoint,
            RegionId = row.Region, MapId = row.MapId, PosX = row.PosX, PosY = row.PosY, PosZ = row.PosZ, Dir = row.Dir };
        ch.Persist.StatLevel = row.StatLevel; ch.Persist.StatPoint = row.StatPoint; ch.Persist.StatExp = row.StatExp;
        var pvp = await map.LoadPvpAsync(2);
        ch.PvpUseablePoint=pvp.Useable; ch.PvpTotalPoint=pvp.Total; Array.Copy(pvp.Record,ch.PvpRecord,ch.PvpRecord.Length);
        ch.MonthPvPoint=pvp.MonthPoint; ch.MonthWin=pvp.MonthWin; ch.MonthLose=pvp.MonthLose;
        var bags = new Dictionary<byte, Inven> { [255] = new() { InvenId = 255 } };
        foreach (var inv in await map.LoadInvensAsync(2)) bags[inv.InvenId] = new() { InvenId = inv.InvenId, TemplateId = inv.TemplateId, EndTime = inv.EndTime, Eld = inv.Eld };
        foreach (var item in await map.LoadItemsAsync(2))
        {
            if (item.StorageType != 0) continue;
            if (!bags.TryGetValue((byte)item.StorageId, out var bag)) bags[(byte)item.StorageId] = bag = new() { InvenId = (byte)item.StorageId };
            var it = new Item { DlId = item.DlId, TemplateId = item.TemplateId, Template = templates.Items[item.TemplateId], Count = item.Count,
                ItemSlot = item.ItemSlot, Level = item.Level, GLevel = item.GLevel, DuraMax = item.DuraMax, DuraCur = item.DuraCur,
                RefineCur = item.RefineCur, GradeEffect = item.GradeEffect, EndTime = item.EndTime, Gem = item.Gem, MoggItemId = item.MoggItemId };
            Array.Copy(item.Ext, it.Ext, 6);
            for (int i = 0; i < 6; i++) if (item.Magic[i] != 0) it.Magic.Add(new(item.Magic[i], item.Value[i]));
            bag.Items.Add(it);
        }
        ch.Invens.AddRange(bags.Values);
        foreach (var sk in await map.LoadSkillsAsync(2)) ch.Skills.Add(new Skill { SkillId = sk.SkillId, Level = sk.Level, ReuseRemainTick = sk.RemainTick, Template = templates.Skill(sk.SkillId) });
        var progress = await map.LoadQuestsAsync(2); service.LoadQuestProgress(ch, progress.quests, progress.terms);
        var session = new ClientSession(channel) { CharId = 2, UserId = 19, Key = 22, Channel = 1, IsMain = true, State = EnterState.InGame, Char = ch };
        state.Register(session); service.AddNpc(new Npc { Id = 500, Type = 2, Country = 3, MapId = ch.MapId });
        byte[] Exec(uint id, byte reward = 0, uint choice = 0) => new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(id).WriteByte(reward).WriteUInt32(choice).ToArray();
        switch (args[0])
        {
            case "--full-quest-enqueue-failure":
                try { await service.DispatchClientAsync(session, Exec(1000)); }
                catch (SqlException)
                {
                    if (ch.DbLoaded || !channel.Closed) throw new InvalidOperationException("Unconfirmed quest remained saveable");
                    await service.OnClientDisconnectAsync(session);
                    if (state.FindByChar(2) is not null) throw new InvalidOperationException("Failed quest session was not removed");
                    return;
                }
                throw new InvalidOperationException("Expected SQL queue publication failure");
            case "--full-quest-accept": await service.DispatchClientAsync(session, Exec(1000)); break;
            case "--full-quest-hunt":
                await service.DispatchWorldAsync(new PacketWriter(Msg.MW_MONSTERDIE_REQ).WriteUInt32(2).WriteUInt32(22).WriteUInt32(0).WriteUInt32(0x70001)
                    .WriteFloat(ch.PosX).WriteFloat(ch.PosY).WriteFloat(ch.PosZ).WriteUInt16(500).WriteByte(3).WriteByte(5).WriteByte(1).ToArray()); break;
            case "--full-quest-complete": await service.DispatchClientAsync(session, Exec(1001, 2, 201)); break;
            case "--full-quest-repeat": await service.DispatchClientAsync(session, Exec(1001, 2, 201)); return;
            case "--full-quest-drop": await service.DispatchClientAsync(session, new PacketWriter(Msg.CS_QUESTDROP_REQ).WriteUInt32(1002).ToArray()); break;
            case "--full-quest-reaccept": await service.DispatchClientAsync(session, Exec(1002)); break;
            case "--full-quest-expire": await service.DispatchClientAsync(session, Exec(1100)); await service.OnTimerAsync(); break;
            case "--full-quest-expired-check":
                if (!ch.Quests[1100].TimerExpired) throw new InvalidOperationException("Expired mission was revived after restart");
                await service.DispatchClientAsync(session, Exec(1101));
                if (ch.Quests[1100].CompleteCount != 0) throw new InvalidOperationException("Expired mission was completed");
                return;
            default: throw new InvalidOperationException("Unknown quest lifecycle mode");
        }
        // No logout/SaveAll: the event checkpoint alone must survive immediate process exit.
        Environment.Exit(73);
    }

    public static async Task RunAsync(string connection, string directory, List<string> passed)
    {
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task Exec(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        async Task<long> Number(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
        async Task Run(string mode, int exit = 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--full-quest-" + mode); start.ArgumentList.Add(directory);
            using var child = Process.Start(start)!; var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync(); await output; var stderr=await error;
            Require(child.ExitCode == exit, $"Quest lifecycle child {mode} failed ({child.ExitCode}): {stderr}");
        }
        async Task CrashAndRecover(string action)
        {
            await Exec("CREATE TRIGGER FixtureQuestLifecyclePause ON TQUESTTABLE AFTER INSERT,UPDATE AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE dwCharID=2) WAITFOR DELAY '00:00:20'; END");
            try
            {
                await Run(action, 73);
                Require(await SqlQueueFixture.Pending(connection, directory) > 0, "Quest event returned without a durable SQL checkpoint");
            }
            finally
            {
                int waiting = (int)await Number("SELECT COALESCE(MAX(session_id),0) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND wait_type='WAITFOR'");
                if (waiting != 0) await Exec($"IF EXISTS(SELECT 1 FROM sys.dm_exec_requests WHERE session_id={waiting} AND wait_type='WAITFOR') EXEC('KILL {waiting}')");
                await Exec("DROP TRIGGER FixtureQuestLifecyclePause");
            }
            await Run("recover");
        }
        // Clone ONLY the synthetic header inside this disposable instance into a separate synthetic account.
        await Exec(@"DECLARE @cols nvarchar(max),@vals nvarchar(max),@identity bit=0;
SELECT @cols=STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(name)),','),
 @vals=STRING_AGG(CONVERT(nvarchar(max),CASE name WHEN 'dwCharID' THEN '2' WHEN 'dwUserID' THEN '19' WHEN 'szName' THEN '''QuestFixture''' ELSE QUOTENAME(name) END),',')
FROM sys.columns WHERE object_id=OBJECT_ID('TCHARTABLE') AND is_computed=0 AND system_type_id<>189;
IF EXISTS(SELECT 1 FROM sys.identity_columns WHERE object_id=OBJECT_ID('TCHARTABLE')) SET @identity=1;
IF @identity=1 SET IDENTITY_INSERT TCHARTABLE ON;
EXEC('INSERT TCHARTABLE('+@cols+') SELECT '+@vals+' FROM TCHARTABLE WHERE dwCharID=1');
IF @identity=1 SET IDENTITY_INSERT TCHARTABLE OFF;");
        long beforeMoney = await Number("SELECT CONVERT(bigint,dwGold)*10000+dwSilver*100+dwCooper FROM TCHARTABLE WHERE dwCharID=2");
        await Exec($"CREATE TRIGGER FixtureOrdinaryQuestQueueReject ON dbo.TCharacterSaveQueue AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE OwnerKey=N'{directory.Replace("'", "''")}') THROW 51043,'Injected ordinary quest enqueue failure',1; END");
        try { await Run("enqueue-failure"); }
        finally { await Exec("DROP TRIGGER FixtureOrdinaryQuestQueueReject"); }
        Require(await Number("SELECT COUNT(*) FROM TQUESTTABLE WHERE dwCharID=2") == 0 &&
            await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE dwOwnerID=2") == 0 &&
            await SqlQueueFixture.Pending(connection, directory) == 0, "Unconfirmed ordinary quest leaked starter/progress through a separate save");
        passed.Add("ordinary quest acceptance/starter item shares one SQL queue commit; rejected enqueue quarantines and disconnects without partial persistence");
        await CrashAndRecover("accept");
        Require(await Number("SELECT bTriggerCount FROM TQUESTTABLE WHERE dwCharID=2 AND dwQuestID=1000") == 1 &&
            await Number("SELECT bCount FROM TITEMTABLE WHERE dwOwnerID=2 AND wItemID=200") == 1, "Accepted quest/starter item did not recover");
        passed.Add("real quest acceptance/starter item survives immediate process exit without logout or periodic save");
        await CrashAndRecover("hunt");
        Require(await Number("SELECT bCount FROM TQUESTTERMTABLE WHERE dwCharID=2 AND dwQuestID=1000 AND dwTermID=500") == 1, "World hunt progress did not recover");
        passed.Add("World hunt objective checkpoints and reloads in a new process");
        await CrashAndRecover("complete");
        Require(await Number("SELECT bCompleteCount FROM TQUESTTABLE WHERE dwCharID=2 AND dwQuestID=1000") == 1 &&
            await Number("SELECT bTriggerCount FROM TQUESTTABLE WHERE dwCharID=2 AND dwQuestID=1002") == 1, "Completion or next mission was lost");
        Require(await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE dwOwnerID=2 AND wItemID=200") == 0 &&
            await Number("SELECT bCount FROM TITEMTABLE WHERE dwOwnerID=2 AND wItemID=201") == 2, "Ingredients/reward items diverged");
        Require(await Number("SELECT bLevel FROM TSKILLTABLE WHERE dwCharID=2 AND wSkillID=300") == 1 &&
            await Number("SELECT CONVERT(bigint,dwGold)*10000+dwSilver*100+dwCooper FROM TCHARTABLE WHERE dwCharID=2") == beforeMoney + 37, "Skill/money reward was lost or duplicated");
        passed.Add("selected turn-in reward, ingredients, money, learned skill, completion and next mission recover together");
        var pvpAfter = await new GameDatabase(connection).LoadPvpAsync(2);
        Require(pvpAfter.Useable == 150 && pvpAfter.Total == 0 && pvpAfter.MonthPoint == 0,
            "Achievement reward was lost or changed honour/monthly balances during full checkpoint recovery");
        passed.Add("exact quest achievement points share the full SQL checkpoint with completion and recover after process exit");
        await Run("repeat");
        Require(await Number("SELECT CONVERT(bigint,dwGold)*10000+dwSilver*100+dwCooper FROM TCHARTABLE WHERE dwCharID=2") == beforeMoney + 37 &&
            await Number("SELECT bCount FROM TITEMTABLE WHERE dwOwnerID=2 AND wItemID=201") == 2, "Reloaded quest paid a second reward");
        passed.Add("completed mission cannot be rewarded again after a fresh-process reload");
        Require((await new GameDatabase(connection).LoadPvpAsync(2)).Useable == 150,
            "Completed quest duplicated or erased achievement points after fresh-process replay");
        passed.Add("quest achievement points reload through native PvP procedures without a second reward");
        await CrashAndRecover("drop");
        Require(await Number("SELECT bTriggerCount FROM TQUESTTABLE WHERE dwCharID=2 AND dwQuestID=1002") == 0, "Abandoned quest did not persist");
        await CrashAndRecover("reaccept");
        Require(await Number("SELECT bTriggerCount FROM TQUESTTABLE WHERE dwCharID=2 AND dwQuestID=1002") == 1, "Reaccepted quest did not persist");
        passed.Add("abandonment and reacceptance each survive a separate process exit/recovery");
        await CrashAndRecover("expire"); await Run("expired-check");
        Require(await Number("SELECT dwTick FROM TQUESTTABLE WHERE dwCharID=2 AND dwQuestID=1100") == 0 &&
            await Number("SELECT bCompleteCount FROM TQUESTTABLE WHERE dwCharID=2 AND dwQuestID=1100") == 0, "Expired timer revived after reload");
        passed.Add("server timer expiry persists zero remaining and stays failed across process reload/turn-in");
    }
    private sealed class Channel : IClientChannel
    {
        public bool Closed;
        public IPEndPoint? RemoteEndPoint => null;
        public void Send(byte[] packet) { }
        public void Send(PacketWriter packet) { }
        public void Close() { Closed = true; }
    }
    private sealed class World : IWorldSink
    {
        public void Send(byte[] packet) { }
        public void Send(PacketWriter packet) { }
    }
}
