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

// FullSaveCheck verifies the disposable host/catalog/marker before invoking either entry point.
internal static class QuestMailRecoveryCheck
{
    private sealed class FaultStore(GameDatabase db, string mode) : IQuestPostOperationStore
    {
        public Task<QuestPostResult> ReserveQuestPostAsync(uint q, uint c, IReadOnlyList<ItemSaveData> i, byte max = 0)
            => db.ReserveQuestPostAsync(q, c, i, max);
        public async Task<QuestPostResult> ReserveQuestPostAsync(QuestPostIntent intent)
        {
            var result = await db.ReserveQuestPostAsync(intent);
            if (mode == "before-children" && intent.QuestId == 2000) Environment.Exit(74);
            return result;
        }
        public Task<IReadOnlyList<QuestPostOperation>> LoadQuestPostOperationsAsync(uint c) => db.LoadQuestPostOperationsAsync(c);
        public async Task AcknowledgeQuestPostOperationsAsync(uint c, IReadOnlyList<Guid> ids)
        {
            if (ids.Count != 0)
            {
                if (mode == "after-snapshot") Environment.Exit(75);
                if (mode == "sql-failure") throw new IOException("Injected failure before continuation acknowledgement");
            }
            await db.AcknowledgeQuestPostOperationsAsync(c, ids);
            if (ids.Count != 0 && mode == "after-ack") Environment.Exit(76);
        }
    }
    private sealed class PointRandom(Action point) : Random
    {
        private int calls;
        public override int Next(int maxValue)
        {
            if (++calls == 3) point(); // two initial mail reward rolls, then the skill gate after GiveItem
            return 0;
        }
    }
    private sealed class Channel : IClientChannel
    {
        public bool Closed;
        public List<ushort> Sent = new();
        public IPEndPoint? RemoteEndPoint => null;
        public void Send(byte[] p) => Sent.Add(PacketHeader.ReadId(p));
        public void Send(PacketWriter p) => Send(p.ToArray());
        public void Close() => Closed = true;
    }
    private sealed class World : IWorldSink
    {
        public void Send(byte[] p) { }
        public void Send(PacketWriter p) { }
    }
    public static async Task ChildAsync(string[] args, string connection)
    {
        string mode = args[0]["--full-mail-".Length..]; uint charId = uint.Parse(args[2]);
        var t = new TemplateStore();
        foreach (ushort id in new ushort[] { 200, 201 }) t.Items[id] = new(id, 0, new float[4], Stack: 10, ClassId: uint.MaxValue);
        t.Skills[300] = new(300, 0, 0, 0, 0, 0, 1, 10, 1, 0, 0, 0, 0, 0, 1, 0, ClassId: uint.MaxValue);
        QuestTemplate Add(uint id, QuestType type, uint parent = 0, byte trigger = 1, uint key = 0)
        {
            var q = new QuestTemplate { QuestId = id, Type = (byte)type, ParentId = parent, TriggerType = trigger,
                TriggerId = key == 0 ? parent : key, CountMax = type == QuestType.Craft ? (byte)0 : (byte)1 };
            t.Quests[id] = q; return q;
        }
        var start = Add(1999, QuestType.Mission, trigger: 3, key: 500);
        start.Terms.Add(new(201, 2, 1)); start.Terms.Add(new(201, 5, 1));
        Add(2000, QuestType.SendPost, 1999).Rewards.Add(new(200, 2, 1, 0, 1));
        Add(2001, QuestType.Mission, 2000).Terms.Add(new(501, 3, 1));
        Add(2002, QuestType.GiveItem, 2000).Terms.Add(new(201, 5, 2));
        if (charId == 9) Add(2006, QuestType.Switch, 2000).Terms.Add(new(999, 15, 1));
        var skill = Add(2003, QuestType.GiveSkill, 2000); skill.Terms.Add(new(300, 4, 1));
        skill.Conditions.Add(new(100, 12, 0)); // guaranteed eligible, with a deterministic interruption point
        Add(2004, QuestType.SendPost, 2000).Rewards.Add(new(200, 2, 1, 0, 2));
        Add(2005, QuestType.Craft, 2004).Rewards.Add(new(41, 1, 1, 0, 0));
        Add(2007, QuestType.SendPost, 1999).Rewards.Add(new(200, 2, 1, 0, 1));
        var opt = new MapServerOptions { SaveQueueName = args[1] };
        opt.Db.GameConnectionString = connection;
        opt.Db.GlobalConnectionString = new SqlConnectionStringBuilder(connection) { InitialCatalog = "TGlobal_gsp" }.ConnectionString;
        var db = new GameDatabase(connection); var state = new MapState(); var channel = new Channel();
        var service = new MapService(opt, state, new World(), db, t, NullLogger<MapService>.Instance)
        { QuestPostStore = new FaultStore(db, mode) };
        service.InitQuests(); service.ApplyItemIdSeed(2000000000L + charId * 1000);
        using var journal = service.OpenSaveQueue(); await service.RecoverPendingSavesAsync();
        var row = await db.LoadCharAsync(charId) ?? throw new InvalidOperationException("Missing mail fixture character");
        var ch = new Character { CharId = charId, DbLoaded = true, Name = row.Name, Class = row.Class, Country = row.Country,
            Level = row.Level, Gold = row.Gold, Silver = row.Silver, Cooper = row.Cooper, Exp = row.Exp, Hp = row.Hp, Mp = row.Mp,
            MapId = row.MapId, PosX = row.PosX, PosY = row.PosY, PosZ = row.PosZ, SkillPoint = row.SkillPoint };
        ch.Persist.StatLevel = row.StatLevel; ch.Persist.StatPoint = row.StatPoint; ch.Persist.StatExp = row.StatExp;
        ch.Invens.Add(new Inven { InvenId = 255 });
        foreach (var item in await db.LoadItemsAsync(charId))
            if (item.StorageType == 0) ch.Invens[0].Items.Add(new Item { DlId = item.DlId, TemplateId = item.TemplateId,
                Template = t.Items[item.TemplateId], Count = item.Count, ItemSlot = item.ItemSlot });
        foreach (var sk in await db.LoadSkillsAsync(charId)) ch.Skills.Add(new Skill { SkillId = sk.SkillId, Level = sk.Level, Template = t.Skill(sk.SkillId) });
        var progress = await db.LoadQuestsAsync(charId); service.LoadQuestProgress(ch, progress.quests, progress.terms);
        var s = new ClientSession(channel) { CharId = charId, UserId = 29 + charId, Key = 22, Channel = 1,
            IsMain = true, EntryValidated = true, State = mode == "recover" ? EnterState.Granted : EnterState.InGame, Char = ch };
        state.Register(s); service.AddNpc(new Npc { Id = 500, Country = 3, Type = 2, MapId = ch.MapId });
        if (mode == "before-publication") service.QuestRng = new PointRandom(() => Environment.Exit(78));
        try
        {
            if (mode == "recover")
            {
                // The actual admission hook, before world placement, not just a direct test helper call.
                await service.DispatchClientAsync(s, new PacketWriter(Msg.CS_CONREADY_REQ).ToArray());
                if (s.State != EnterState.InGame || channel.Closed) throw new InvalidOperationException("Recovered main was not admitted");
            }
            else await service.DispatchClientAsync(s, new PacketWriter(Msg.CS_QUESTEXEC_REQ).WriteUInt32(1999).WriteByte(0).WriteUInt32(0).ToArray());
        }
        catch when (mode is "sql-failure" or "unsupported" or "enqueue-failure" or "initial-enqueue-failure")
        {
            if (!channel.Closed || ch.DbLoaded || channel.Sent.Contains(Msg.CS_ADDITEM_ACK))
                throw new InvalidOperationException("Failed continuation leaked a saveable character or successful child reply");
            await service.OnClientDisconnectAsync(s); // must not save the failed in-memory child state
            Environment.Exit(77);
        }
        if (mode is not ("recover" or "run")) throw new InvalidOperationException("Expected mail interruption did not happen");
    }

    public static async Task RunAsync(string connection, string directory, List<string> passed)
    {
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task Exec(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        async Task<long> Number(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
        async Task Run(string mode, uint owner, int expected = 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var arg in new[] { "--full-mail-" + mode, directory + "-" + owner, owner.ToString() }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync(); await output; string detail = await error;
            Require(child.ExitCode == expected, $"Mail child {mode}/{owner} exit {child.ExitCode}: {detail}");
        }
        await Exec("INSERT TQTITLECHART(dwQuestID,szTitle,szMessage,szNPCName) VALUES(2000,'Root','Root reward','Fixture'),(2004,'Nested','Nested reward','Fixture'),(2007,'Sibling','Sibling reward','Fixture'),(2100,'Identity','Identity reward','Fixture')");
        foreach (uint owner in new uint[] { 3, 4, 5, 6, 7, 8, 9, 10, 12 })
        {
            await Exec($@"DECLARE @cols nvarchar(max),@vals nvarchar(max);
SELECT @cols=STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(name)),',') WITHIN GROUP(ORDER BY column_id),
 @vals=STRING_AGG(CONVERT(nvarchar(max),CASE name WHEN 'dwCharID' THEN '{owner}' WHEN 'dwUserID' THEN '{29 + owner}' WHEN 'szName' THEN '''MailFixture{owner}''' ELSE QUOTENAME(name) END),',') WITHIN GROUP(ORDER BY column_id)
FROM sys.columns WHERE object_id=OBJECT_ID('TCHARTABLE') AND is_computed=0 AND system_type_id<>189;
IF EXISTS(SELECT 1 FROM sys.identity_columns WHERE object_id=OBJECT_ID('TCHARTABLE')) SET IDENTITY_INSERT TCHARTABLE ON;
EXEC('INSERT TCHARTABLE('+@cols+') SELECT '+@vals+' FROM TCHARTABLE WHERE dwCharID=1');
IF EXISTS(SELECT 1 FROM sys.identity_columns WHERE object_id=OBJECT_ID('TCHARTABLE')) SET IDENTITY_INSERT TCHARTABLE OFF;");
        }
        var modes = new[] { ("before-children", 74), ("before-publication", 78), ("after-snapshot", 75), ("after-ack", 76), ("sql-failure", 77), ("enqueue-failure", 77) };
        uint charId = 3;
        foreach (var (mode, exit) in modes)
        {
            long money = await Number($"SELECT CONVERT(bigint,dwGold)*10000+dwSilver*100+dwCooper FROM TCHARTABLE WHERE dwCharID={charId}");
            if (mode == "enqueue-failure")
                await Exec($"CREATE TRIGGER FixtureSaveQueueFailure ON dbo.TCharacterSaveQueue AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE OwnerKey=N'{(directory + "-" + charId).Replace("'", "''")}' AND JSON_VALUE(Payload,'$.Character.QuestPostReceipts[0]') IS NOT NULL) THROW 51043,'Injected SQL enqueue failure',1; END");
            try { await Run(mode, charId, exit); }
            finally { if (mode == "enqueue-failure") await Exec("DROP TRIGGER FixtureSaveQueueFailure"); }
            if (mode is "before-publication" or "enqueue-failure")
                Require(await Number($"SELECT SUM(CONVERT(int,bCount)) FROM TITEMTABLE WHERE dwOwnerID={charId}") == 1, "Unpublished child item escaped into SQL");
            await Run("recover", charId); await Run("recover", charId);
            Require(await Number($"SELECT COUNT(*) FROM TRESERVEDPOST WHERE dwRecverID={charId}") == 3, "Parent, sibling or nested mail was duplicated/lost");
            Require(await Number($"SELECT COUNT(*) FROM TQuestPostOperation WHERE CharId={charId} AND Completed=0") == 0, "Continuation was not acknowledged");
            Require(await Number($"SELECT bCount FROM TITEMTABLE WHERE dwOwnerID={charId} AND wItemID=201") == 3 &&
                await Number($"SELECT bLevel FROM TSKILLTABLE WHERE dwCharID={charId} AND wSkillID=300") == 1 &&
                await Number($"SELECT bTriggerCount FROM TQUESTTABLE WHERE dwCharID={charId} AND dwQuestID=2001") == 1, "Child state was duplicated/lost");
            Require(await Number($"SELECT CONVERT(bigint,dwGold)*10000+dwSilver*100+dwCooper FROM TCHARTABLE WHERE dwCharID={charId}") == money + 41, "Repeatable child craft paid twice or was lost");
            Require(await SqlQueueFixture.Pending(connection, directory + "-" + charId) == 0, "Recovered SQL save queue was not acknowledged");
            passed.Add($"mail chain recovers after {mode}: parent/sibling/nested mail and mission/item/skill/craft result each once; second login unchanged");
            charId++;
        }
        await Run("unsupported", 9, 77);
        Require(await Number("SELECT COUNT(*) FROM TQuestPostOperation WHERE CharId=9 AND Completed=0") == 2 &&
            await Number("SELECT bCount FROM TITEMTABLE WHERE dwOwnerID=9") == 1, "Unsupported world continuation was lost or leaked partial child state");
        passed.Add("unsupported non-durable world action retains pending event, discards child replies and cannot leak items through disconnect");
        await Exec($"CREATE TRIGGER FixtureInitialQueueFailure ON dbo.TCharacterSaveQueue AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE OwnerKey=N'{(directory + "-12").Replace("'", "''")}') THROW 51043,'Injected initial SQL enqueue failure',1; END");
        try { await Run("initial-enqueue-failure", 12, 77); }
        finally { await Exec("DROP TRIGGER FixtureInitialQueueFailure"); }
        Require(await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE dwOwnerID=12") == 0 &&
            await Number("SELECT COUNT(*) FROM TRESERVEDPOST WHERE dwRecverID=12") == 0 &&
            await Number("SELECT COUNT(*) FROM TQUESTTABLE WHERE dwCharID=12") == 0,
            "Initial WAL failure published a starter item/mail/quest through separate saves");
        await Run("run", 12);
        Require(await Number("SELECT bCount FROM TITEMTABLE WHERE dwOwnerID=12") == 3 &&
            await Number("SELECT COUNT(*) FROM TRESERVEDPOST WHERE dwRecverID=12") == 3, "Fresh attempt after failed initial publication diverged");
        passed.Add("initial checkpoint publication failure leaks neither starter items nor mail/quest state through direct or disconnect saves; fresh attempt succeeds");
        var db = new GameDatabase(connection);
        var item = new ItemSaveData(0, 0, 0, 0, 200, 0, 1, 0, 0, 0, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0);
        var intent = new QuestPostIntent(Guid.NewGuid(), 2100, 10, 0, 0, 1, 1, 0, 0, 0, new string('A', 64), [], [item]);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new GameDatabase(connection).ReserveQuestPostAsync(intent)));
        Require(results.All(r => r.Result == 0 && r.Count == 1) && await Number("SELECT COUNT(*) FROM TRESERVEDPOST WHERE dwRecverID=10") == 1, "Uncapped operation identity replayed under concurrency");
        bool mismatch = false;
        try { await db.ReserveQuestPostAsync(intent with { Target = 123 }); } catch (InvalidDataException) { mismatch = true; }
        Require(mismatch, "Mismatched payload reused an issuance identity");
        passed.Add("same uncapped issuance ID deduplicates concurrent/ambiguous replay; changed payload is rejected");
        await Exec("ALTER TABLE TQuestPostOperation ADD CONSTRAINT FixtureOutboxFailure CHECK(CharId<>10 OR IssuedCount<2)");
        try
        {
            bool failed = false;
            try { await db.ReserveQuestPostAsync(intent with { OperationId = Guid.NewGuid() }); } catch (SqlException) { failed = true; }
            Require(failed && await Number("SELECT COUNT(*) FROM TRESERVEDPOST WHERE dwRecverID=10") == 1 &&
                await Number("SELECT bCompleteCount FROM TQUESTTABLE WHERE dwCharID=10 AND dwQuestID=2100") == 1, "Outbox failure did not roll back mail/counter");
        }
        finally { await Exec("ALTER TABLE TQuestPostOperation DROP CONSTRAINT FixtureOutboxFailure"); }
        passed.Add("mail attachments, completed counter and pending continuation roll back together on outbox-write failure");
        await Exec("EXEC sp_rename 'dbo.TQuestPostOperation','FixtureHiddenPostOperation'");
        try
        {
            bool failed = false;
            try { await db.LoadQuestPostOperationsAsync(10); } catch (SqlException) { failed = true; }
            Require(failed, "Missing outbox was silently treated as an empty queue");
        }
        finally { await Exec("EXEC sp_rename 'dbo.FixtureHiddenPostOperation','TQuestPostOperation'"); }
        passed.Add("missing required outbox schema throws rather than admitting with an empty continuation queue; migration 042 applies twice");
    }
}
