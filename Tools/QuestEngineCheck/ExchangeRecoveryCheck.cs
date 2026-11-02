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

// FullSaveCheck validates the disposable SQL server name and database marker before dispatching here.
internal static class ExchangeRecoveryCheck
{
    public static async Task ChildAsync(string[] args, string connection)
    {
        var options = new MapServerOptions { SaveQueueName = args[1] };
        options.Db.GameConnectionString = connection;
        options.Db.GlobalConnectionString = new SqlConnectionStringBuilder(connection) { InitialCatalog = "TGlobal_gsp" }.ConnectionString;
        var db = new GameDatabase(connection, options.Db.GlobalConnectionString); var state = new MapState(); var t = new TemplateStore();
        foreach (ushort id in new ushort[] { 200, 201 }) t.Items[id] = new(id, 0, new float[4], IsSell: 1);
        var service = new MapService(options, state, new World(), db, t, NullLogger<MapService>.Instance);
        using var lease = service.OpenSaveQueue();
        if (args[0] == "--full-exchange-recover") { await service.RecoverPendingSavesAsync(); return; }
        var clients = new List<Client>();
        async Task<ClientSession> Load(uint id)
        {
            var row = await db.LoadCharAsync(id) ?? throw new InvalidDataException("Exchange fixture character missing");
            var ch = new Character { CharId = id, Name = row.Name, DbLoaded = true, Country = row.Country, AidCountry = 3,
                Level = row.Level, Hp = row.Hp, MaxHp = 100, Mp = row.Mp, Gold = row.Gold, Silver = row.Silver, Cooper = row.Cooper };
            var bag = new Inven { InvenId = 255, SlotCount = 16 }; ch.Invens.Add(bag);
            foreach (var item in await db.LoadItemsAsync(id))
            {
                var it = new Item { DlId = item.DlId, TemplateId = item.TemplateId, Template = t.Items[item.TemplateId], ItemSlot = item.ItemSlot,
                    Count = item.Count, Level = item.Level, GLevel = item.GLevel, DuraMax = item.DuraMax, DuraCur = item.DuraCur,
                    RefineCur = item.RefineCur, EndTime = item.EndTime, GradeEffect = item.GradeEffect, Gem = item.Gem, MoggItemId = item.MoggItemId };
                Array.Copy(item.Ext, it.Ext, 6);
                for (int i = 0; i < 6; i++) if (item.Magic[i] != 0) it.Magic.Add(new(item.Magic[i], item.Value[i]));
                bag.Items.Add(it);
            }
            var client = new Client(); clients.Add(client);
            var session = new ClientSession(client) { CharId = id, UserId = id, Key = id, IsMain = true, State = EnterState.InGame, Channel = 1, Char = ch };
            state.Register(session); return session;
        }
        var a = await Load(9001); var b = await Load(9002);
        await service.InitItemIdSeedAsync();
        await service.DispatchClientAsync(a, new PacketWriter(Msg.CS_DEALITEMASK_REQ).WriteString(b.Char!.Name).ToArray());
        await service.DispatchClientAsync(b, new PacketWriter(Msg.CS_DEALITEMRLY_REQ).WriteByte(0).WriteString(a.Char!.Name).ToArray());
        byte[] Offer(uint coins) => new PacketWriter(Msg.CS_DEALITEMADD_REQ).WriteUInt32(0).WriteUInt32(0).WriteUInt32(coins)
            .WriteByte(1).WriteByte(255).WriteByte(0).ToArray();
        await service.DispatchClientAsync(a, Offer(30)); await service.DispatchClientAsync(b, Offer(50));
        var confirm = new PacketWriter(Msg.CS_DEALITEM_REQ).WriteByte(1).ToArray();
        await service.DispatchClientAsync(a, confirm); foreach (var client in clients) client.Packets.Clear();
        await service.DispatchClientAsync(b, confirm);
        if (args[0] == "--full-exchange-reject")
        {
            if (clients.Any(c => !c.Closed || c.Packets.Any(p => PacketHeader.ReadId(p) == Msg.CS_DEALITEMEND_ACK)) || a.Char!.DbLoaded || b.Char!.DbLoaded)
                throw new InvalidOperationException("Rejected exchange leaked success or saveable character state");
            await service.OnClientDisconnectAsync(a); await service.OnClientDisconnectAsync(b); service.FlushItemDirect(); return;
        }
        if (clients.Any(c => c.Closed || !c.Packets.Any(p => PacketHeader.ReadId(p) == Msg.CS_DEALITEMEND_ACK)))
            throw new InvalidOperationException("Exchange was not durably accepted before success");
        if (args[0] == "--full-exchange-crash")
        {
            await service.RecoverPendingSavesAsync(); throw new InvalidOperationException("Expected held SQL stage before process kill");
        }
        try { await service.RecoverPendingSavesAsync(); }
        catch (IOException) when (args[0] == "--full-exchange-fail") { return; }
        throw new InvalidOperationException("Expected exchange SQL failure");
    }

    public static async Task RunAsync(string connection, string scope, List<string> passed)
    {
        async Task Exec(string sql)
        { await using var db = new SqlConnection(connection); await db.OpenAsync(); await using var cmd = new SqlCommand(sql, db); await cmd.ExecuteNonQueryAsync(); }
        async Task<long> Number(string sql)
        { await using var db = new SqlConnection(connection); await db.OpenAsync(); await using var cmd = new SqlCommand(sql, db); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
        void Require(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
        Process Child(string mode)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(mode); start.ArgumentList.Add(scope); return Process.Start(start)!;
        }
        async Task Run(string mode)
        {
            using var p = Child(mode); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));
            Require(p.ExitCode == 0, "Exchange child failed: " + await stdout + await stderr);
        }
        await Exec(@"
DECLARE @columns nvarchar(max), @values nvarchar(max), @sql nvarchar(max);
SELECT @columns=STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(name)),',') WITHIN GROUP(ORDER BY column_id),
       @values=STRING_AGG(CONVERT(nvarchar(max),CASE name WHEN 'dwCharID' THEN '9001' WHEN 'dwUserID' THEN '9001' WHEN 'szNAME' THEN '''DealAlice''' ELSE QUOTENAME(name) END),',') WITHIN GROUP(ORDER BY column_id)
FROM sys.columns WHERE object_id=OBJECT_ID('dbo.TCHARTABLE') AND is_computed=0;
SET @sql=N'SET IDENTITY_INSERT TCHARTABLE ON; INSERT TCHARTABLE('+@columns+') SELECT '+@values+' FROM TCHARTABLE WHERE dwCharID=1; SET IDENTITY_INSERT TCHARTABLE OFF;'; EXEC(@sql);
SET @sql=REPLACE(REPLACE(@sql,'9001','9002'),'DealAlice','DealBob'); EXEC(@sql);
UPDATE TCHARTABLE SET dwGold=0,dwSilver=0,dwCooper=CASE dwCharID WHEN 9001 THEN 100 ELSE 200 END,bLevel=5,dwHP=100,dwMP=100 WHERE dwCharID IN(9001,9002);
");
        var map = new GameDatabase(connection);
        ItemSaveData Item(uint owner, ushort item) => new(940000000 + owner, 0, 255, 0, item, 7, 1, 4, 500, 450, 6, 0, 8,
            [1,2,3,4,5,6], [101,102,103,104,105,106], [11,12,13,14,15,16], 9, 201);
        foreach (var (id, item) in new (uint, ushort)[] { (9001, 200), (9002, 201) })
            await map.SaveInventoryAsync(id, [new(255, 0, 0, 0)], [Item(id, item)], requireProcedures: true);
        await Exec("CREATE TRIGGER FixtureExchangePause ON TCHARTABLE AFTER UPDATE AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE dwCharID=9001) WAITFOR DELAY '00:00:20'; END");
        int waiting = 0;
        using (var writer = Child("--full-exchange-crash"))
        {
            var stdout = writer.StandardOutput.ReadToEndAsync(); var stderr = writer.StandardError.ReadToEndAsync(); var timer = Stopwatch.StartNew();
            try
            {
                while (timer.Elapsed < TimeSpan.FromSeconds(15) && !writer.HasExited)
                {
                    waiting = (int)await Number("SELECT COALESCE(MAX(session_id),0) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND wait_type='WAITFOR'");
                    if (waiting != 0) break; await Task.Delay(50);
                }
                Require(waiting != 0, "Exchange writer never reached the second-character pause: " + (writer.HasExited ? await stdout + await stderr : "timeout"));
            }
            finally
            {
                if (!writer.HasExited) writer.Kill(entireProcessTree: true); await writer.WaitForExitAsync();
                if (waiting != 0) await Exec($"IF EXISTS(SELECT 1 FROM sys.dm_exec_requests WHERE session_id={waiting} AND database_id=DB_ID() AND wait_type='WAITFOR') EXEC('KILL {waiting}')");
                await Exec("DROP TRIGGER FixtureExchangePause");
            }
        }
        Require(await SqlQueueFixture.Pending(connection, scope) == 1, "Killed exchange lost its shared SQL record");
        Require(await Number("SELECT dwCooper FROM TCHARTABLE WHERE dwCharID=9002") == 180 && await Number("SELECT dwCooper FROM TCHARTABLE WHERE dwCharID=9001") == 100,
            "Did not interrupt between the independently committed character saves");
        passed.Add("real trade dispatch freezes both players in one SQL record; process killed between participant saves retains it");
        async Task Verify()
        {
            Require(await Number("SELECT dwCooper FROM TCHARTABLE WHERE dwCharID=9001") == 120 && await Number("SELECT dwCooper FROM TCHARTABLE WHERE dwCharID=9002") == 180, "Exchange balances diverged");
            foreach (var (id, item) in new (uint, ushort)[] { (9001, 201), (9002, 200) })
            {
                var it = (await new GameDatabase(connection).LoadItemsAsync(id)).Single();
                Require(it.TemplateId == item && it.Count == 1 && it.Gem == 9 && it.Magic[1] == 2 && it.Value[1] == 102 && it.Ext[1] == 12 && it.DuraCur == 450,
                    "Exchange item/attributes failed fresh-connection reload");
            }
            Require(await SqlQueueFixture.Pending(connection, scope) == 0, "Complete exchange not acknowledged");
        }
        await Run("--full-exchange-recover"); await Verify();
        passed.Add("fresh process replays both native character/inventory callbacks and preserves total currency and every item attribute");
        await Run("--full-exchange-recover"); await Verify();
        passed.Add("acknowledged exchange cannot be replayed into duplicate items or currency");
        await Exec("CREATE TRIGGER FixtureExchangeReject ON TCharacterSaveQueue INSTEAD OF INSERT AS BEGIN THROW 51048,'Injected exchange enqueue failure',1; END");
        try { await Run("--full-exchange-reject"); } finally { await Exec("DROP TRIGGER FixtureExchangeReject"); }
        await Verify(); passed.Add("failed durable publication sends no success, quarantines both players and cannot leak through logout/item saves");
        await Exec("CREATE TRIGGER FixtureExchangeFailure ON TCHARTABLE AFTER UPDATE AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE dwCharID=9001) THROW 51048,'Injected second-player save failure',1; END");
        try { await Run("--full-exchange-fail"); Require(await SqlQueueFixture.Pending(connection, scope) == 1, "Late exchange failure discarded the pair"); }
        finally { await Exec("DROP TRIGGER FixtureExchangeFailure"); }
        passed.Add("second-player SQL failure retains the complete exchange and the admission barrier rejects partial recovery");
        await Run("--full-exchange-recover");
        Require(await SqlQueueFixture.Pending(connection, scope) == 0 && await Number("SELECT SUM(dwCooper) FROM TCHARTABLE WHERE dwCharID IN(9001,9002)") == 300,
            "Late retry lost currency or durable exchange");
        Require((await map.LoadItemsAsync(9001)).Single().TemplateId == 200 && (await map.LoadItemsAsync(9002)).Single().TemplateId == 201, "Late retry lost/duplicated items");
        passed.Add("another fresh process completes a failed second-player save without losing the swapped items or total currency");
    }
    private sealed class Client : IClientChannel
    {
        public bool Closed; public readonly List<byte[]> Packets = new(); public IPEndPoint? RemoteEndPoint => null;
        public void Send(byte[] p) => Packets.Add(p); public void Send(PacketWriter p) => Send(p.ToArray()); public void Close() => Closed = true;
    }
    private sealed class World : IWorldSink { public void Send(byte[] p) { } public void Send(PacketWriter p) { } }
}
