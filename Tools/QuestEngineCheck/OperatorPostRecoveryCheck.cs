using System.Diagnostics;
using System.Reflection;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

// FullSaveCheck validates the disposable instance/catalog/marker before entering either method.
internal static class OperatorPostRecoveryCheck
{
    private sealed class World : IWorldSink { public void Send(byte[] p) { } public void Send(PacketWriter p) { } }
    private sealed class Client : IClientChannel
    {
        public IPEndPoint? RemoteEndPoint => null;
        public void Send(byte[] p) { } public void Send(PacketWriter p) { } public void Close() { }
    }
    private sealed class FaultStore(GameDatabase db, string mode) : IOperatorPostStore
    {
        public Task VerifyOperatorPostSchemaAsync() => db.VerifyOperatorPostSchemaAsync();
        public async Task<OperatorPostResult> DeliverOperatorPostAsync(OperatorPostIntent intent)
        {
            if (mode == "before") Environment.Exit(81);
            var result = await db.DeliverOperatorPostAsync(intent);
            if (mode == "after") Environment.Exit(82);
            return result;
        }
    }
    public static async Task ChildAsync(string[] args, string connection)
    {
        string mode = args[0]["--full-post-".Length..];
        var global = new SqlConnectionStringBuilder(connection) { InitialCatalog = "TGlobal_gsp" }.ConnectionString;
        var opt = new MapServerOptions { SaveQueueName = args[1] };
        opt.Db.GameConnectionString = connection; opt.Db.GlobalConnectionString = global;
        var db = new GameDatabase(connection, global); var t = new TemplateStore();
        t.Items[200] = new(200, 0, [1, 0, 0, 0], DuraMax: 123);
        t.BattleZones[9] = new(9, "Garden", 900, 0, 0, 0, 0, 0, 0, 0, 0, 200, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        t.Territories.Add(new(LocalType.SkyGarden, 9, 0, 0, "", 0, 0, "", 0));
        var state = new MapState();
        var service = new MapService(opt, state, new World(), db, t, NullLogger<MapService>.Instance)
            { OperatorPostStore = new FaultStore(db, mode) };
        using var queue = service.OpenSaveQueue(); await service.VerifyOperatorPostStorageAsync();
        if (mode != "recover")
        {
            service.InitTerritories();
            // The recipient row exists in SQL; this fixture deliberately isolates mail from a full character save.
            var ch = new Character { CharId = 1, Name = "FullFixture", Country = 1, AidCountry = 3, MapId = 900, Hp = 100, MaxHp = 100 };
            var session = new ClientSession(new Client()) { CharId = 1, Key = 11, IsMain = true, EntryValidated = true,
                State = EnterState.InGame, Channel = 1, Char = ch };
            state.Register(session); state.EnterWorld(session);
            await service.DispatchWorldAsync(new PacketWriter(Msg.MW_SKYGARDENOCCUPY_REQ).WriteByte(1).WriteUInt16(9).WriteByte(1).ToArray());
        }
        try { await service.RecoverPendingSavesAsync(); }
        catch (IOException) when (mode == "failure")
        {
            if (((SqlSaveQueue)queue!).PendingCount != 1) throw new InvalidOperationException("Failed reward SQL lost its durable intent");
            return;
        }
        if (mode is "before" or "after" or "failure") throw new InvalidOperationException("Expected injected reward failure/crash");
    }

    public static async Task RunAsync(string connection, string scope, string migration, List<string> passed)
    {
        void Require(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
        async Task Exec(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        async Task<long> Number(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
        async Task Run(string mode, string owner, int expected = 0)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--full-post-" + mode); start.ArgumentList.Add(owner);
            using var p = Process.Start(start)!; var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Require(p.ExitCode == expected, "Reward child " + mode + " failed: " + await stderr + await stdout);
        }
        OperatorPostIntent Intent(params ItemSaveData[] items) => new(Guid.NewGuid(), 1, "stale recipient name", "00000006Reward", "00000004Body", 1,
            2, 500, 7, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), items.ToList());
        ItemSaveData Item(byte slot = 0, ushort template = 200, byte count = 5) => new(0, 2, 0, slot, template, 7, count, 4, 500, 450, 6, 0, 8,
            [1,2,3,4,5,6], [101,102,103,104,105,106], [11,12,13,14,15,16], 9, 201);
        await SqlQueueFixture.ApplyMigration(connection, migration);
        // Valid native DB allocation domain, synthetic only; it does not share the Map's ID counter.
        await Exec("INSERT TDBITEMINDEXTABLE(bWorld,dlID) VALUES(2,144115188075856872)");
        var db = new GameDatabase(connection); await db.VerifyOperatorPostSchemaAsync();
        await Exec("EXEC sp_rename 'dbo.TOperatorPostOperation','FixtureOperatorPostHidden'");
        try
        {
            bool failed = false; try { await db.VerifyOperatorPostSchemaAsync(); } catch (SqlException) { failed = true; }
            Require(failed, "Missing required server reward receipt schema was silently accepted");
        }
        finally { await Exec("EXEC sp_rename 'dbo.FixtureOperatorPostHidden','TOperatorPostOperation'"); }
        passed.Add("migration 045 applies twice; missing server reward receipt schema fails the startup guard");

        var request = Intent(Item(), Item(1, count: 2));
        var delivered = await db.DeliverOperatorPostAsync(request);
        Require(delivered is { Result: 0, Created: true, Receiver: "FullFixture" } &&
            await Number($"SELECT COUNT(*) FROM TPOSTTABLE WHERE dwPostID={delivered.PostId} AND dwCharID=1 AND dwGold=2 AND dwSilver=500 AND dwCooper=7 AND bContain=1 AND szTitle='00000006Reward'") == 1 &&
            await Number($"SELECT COUNT(*) FROM TITEMTABLE WHERE dwStorageID={delivered.PostId} AND bStorageType=2 AND dwOwnerID=1 AND bGradeEffect=8 AND bMagic2=2 AND wValue2=102 AND dwTime2=12 AND bGem=9 AND wMoggItemID=201") == 2,
            "Atomic reward header/money/new attachments or canonical receiver failed");
        passed.Add("server reward mail atomically stores native header, coins and every attachment attribute, using the recipient ID's current name");
        long noItemIndex = await Number("SELECT dlID FROM TDBITEMINDEXTABLE");
        foreach (byte type in new byte[] { 0, 1 })
        {
            var empty = Intent() with { Type = type, Gold = 0, Silver = type == 0 ? 0u : 500u, Cooper = 0 };
            var letter = await db.DeliverOperatorPostAsync(empty);
            Require(letter.Created && await Number($"SELECT COUNT(*) FROM TPOSTTABLE WHERE dwPostID={letter.PostId} AND bType={type} AND bContain=0 AND dwSilver={empty.Silver}") == 1,
                "Money-only or plain server mail changed type, coins or containment");
        }
        Require(await Number("SELECT dlID FROM TDBITEMINDEXTABLE") == noItemIndex, "Empty mail allocated an item ID");
        passed.Add("plain tournament notifications and money-only reward packages retain their native type/coins without attachments or item-ID allocation");

        var concurrent = Intent(Item());
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => new GameDatabase(connection).DeliverOperatorPostAsync(concurrent)));
        Require(results.Select(r => r.PostId).Distinct().Count() == 1 && results.Count(r => r.Created) == 1, "Concurrent same-operation mail duplicated");
        passed.Add("concurrent independent SQL mail writers create one letter/attachment set for one durable operation ID");
        bool changed = false;
        try { await db.DeliverOperatorPostAsync(concurrent with { Silver = 501 }); } catch (InvalidDataException) { changed = true; }
        Require(changed, "Changed reward payload reused an issued operation ID");
        passed.Add("an issued reward ID rejects changed coins/items instead of silently accepting a different payload");
        uint claimed = results[0].PostId;
        await Exec($"DELETE TITEMTABLE WHERE bStorageType=2 AND dwStorageID={claimed}; DELETE TPOSTTABLE WHERE dwPostID={claimed};");
        var replay = await db.DeliverOperatorPostAsync(concurrent);
        Require(replay.PostId == claimed && !replay.Created && await Number($"SELECT COUNT(*) FROM TPOSTTABLE WHERE dwPostID={claimed}") == 0,
            "Replay re-created a collected/deleted reward");
        passed.Add("receipt replay after collection/deletion cannot re-create the consumed letter, money or items");

        long headers = await Number("SELECT COUNT(*) FROM TPOSTTABLE"), receipts = await Number("SELECT COUNT(*) FROM TOperatorPostOperation"), itemIndex = await Number("SELECT dlID FROM TDBITEMINDEXTABLE");
        var failItem = Intent(Item(), Item(1, 333, 3));
        await Exec("ALTER TABLE TITEMTABLE ADD CONSTRAINT FixtureRewardItemFailure CHECK(wItemID<>333 OR bCount<>3)");
        try
        {
            bool failed = false; try { await db.DeliverOperatorPostAsync(failItem); } catch (SqlException) { failed = true; }
            Require(failed && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers &&
                await Number("SELECT COUNT(*) FROM TOperatorPostOperation") == receipts && await Number("SELECT dlID FROM TDBITEMINDEXTABLE") == itemIndex,
                "Second attachment rejection committed a header/earlier item/receipt or allocation");
        }
        finally { await Exec("ALTER TABLE TITEMTABLE DROP CONSTRAINT FixtureRewardItemFailure"); }
        Require((await db.DeliverOperatorPostAsync(failItem)).Created, "Rolled back reward cannot retry");
        passed.Add("second attachment failure rolls back header, earlier attachment, coins, ID allocation and receipt; the same intent can retry");

        headers = await Number("SELECT COUNT(*) FROM TPOSTTABLE");
        await Exec("CREATE TRIGGER FixtureSilentRewardInsert ON dbo.TITEMTABLE INSTEAD OF INSERT AS BEGIN RETURN; END");
        try
        {
            bool failed = false; try { await db.DeliverOperatorPostAsync(Intent(Item())); } catch (SqlException) { failed = true; }
            Require(failed && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers, "Silent native item rejection committed an empty reward header");
        }
        finally { await Exec("DROP TRIGGER FixtureSilentRewardInsert"); }
        passed.Add("a silently skipped native item insert is detected by row verification and rolls the whole reward transaction back");

        var failReceipt = Intent(Item()); headers = await Number("SELECT COUNT(*) FROM TPOSTTABLE");
        await Exec($"ALTER TABLE TOperatorPostOperation ADD CONSTRAINT FixtureReceiptFailure CHECK(OperationId<>'{failReceipt.OperationId:D}')");
        try
        {
            bool failed = false; try { await db.DeliverOperatorPostAsync(failReceipt); } catch (SqlException) { failed = true; }
            Require(failed && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers, "Receipt failure left a reward without a receipt");
        }
        finally { await Exec("ALTER TABLE TOperatorPostOperation DROP CONSTRAINT FixtureReceiptFailure"); }
        Require((await db.DeliverOperatorPostAsync(failReceipt)).Created, "Failed receipt cannot retry");
        passed.Add("receipt insertion failure rolls native mail/item writes back and permits retry without duplicating rewards");

        itemIndex = await Number("SELECT dlID FROM TDBITEMINDEXTABLE");
        long ownedCount = await Number("SELECT bCount FROM TITEMTABLE WHERE dlID=900000001");
        await Exec("UPDATE TDBITEMINDEXTABLE SET bWorld=0,dlID=900000000");
        try
        {
            bool failed = false; try { await db.DeliverOperatorPostAsync(Intent(Item())); } catch (SqlException) { failed = true; }
            Require(failed && await Number("SELECT bCount FROM TITEMTABLE WHERE dlID=900000001") == ownedCount,
                "Native DB allocator collision overwrote a player's inventory item");
        }
        finally { await Exec($"UPDATE TDBITEMINDEXTABLE SET bWorld=2,dlID={itemIndex}"); }
        passed.Add("a corrupted native DB item allocator cannot delete/replace an already owned inventory item");
        var missing = Intent(Item()) with { CharId = 9999 }; headers = await Number("SELECT COUNT(*) FROM TPOSTTABLE");
        Require((await db.DeliverOperatorPostAsync(missing)).Result == 1 && (await db.DeliverOperatorPostAsync(missing)).Result == 1 &&
            await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers, "Deleted recipient created an orphan reward");
        passed.Add("a missing/deleted recipient receives a durable rejection receipt, without orphan mail or a permanently blocked queue");

        string beforeOwner = scope + "-before"; headers = await Number("SELECT COUNT(*) FROM TPOSTTABLE");
        await Run("before", beforeOwner, 81);
        Require(await SqlQueueFixture.Pending(connection, beforeOwner) == 1 && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers,
            "Reward dispatcher did not commit its complete SQL queue intent before delivery");
        await Run("recover", beforeOwner); await Run("recover", beforeOwner);
        Require(await SqlQueueFixture.Pending(connection, beforeOwner) == 0 && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers + 1,
            "Fresh MapService did not recover the single reward");
        passed.Add("real Sky Garden reward dispatch survives process exit before delivery; a fresh MapService recovers its SQL-queued mail once");

        string afterOwner = scope + "-after"; headers = await Number("SELECT COUNT(*) FROM TPOSTTABLE");
        await Run("after", afterOwner, 82);
        Require(await SqlQueueFixture.Pending(connection, afterOwner) == 1 && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers + 1,
            "Post-commit crash did not leave an acknowledged-by-SQL reward and pending queue entry");
        await Run("recover", afterOwner); await Run("recover", afterOwner);
        Require(await SqlQueueFixture.Pending(connection, afterOwner) == 0 && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == headers + 1,
            "Post-commit recovery duplicated a reward");
        passed.Add("process exit after native reward commit but before queue acknowledgement replays through its SQL receipt without a second letter");

        string failureOwner = scope + "-failure"; headers = await Number("SELECT MAX(dwPostID) FROM TPOSTTABLE");
        await Exec($"ALTER TABLE TITEMTABLE ADD CONSTRAINT FixtureRewardDeliveryFailure CHECK(bStorageType<>2 OR dwStorageID<={headers})");
        try { await Run("failure", failureOwner); Require(await SqlQueueFixture.Pending(connection, failureOwner) == 1, "Failed delivery discarded durable reward"); }
        finally { await Exec("ALTER TABLE TITEMTABLE DROP CONSTRAINT FixtureRewardDeliveryFailure"); }
        await Run("recover", failureOwner);
        Require(await SqlQueueFixture.Pending(connection, failureOwner) == 0, "SQL reward failure could not recover in a new process");
        passed.Add("injected real reward attachment SQL failure retains its SQL queue entry; another process completes it after the fault is removed");
    }
}
