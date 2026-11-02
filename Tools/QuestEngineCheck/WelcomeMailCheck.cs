using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using TMap.Data;
using TMap.Protocol;
using TMap.Server;
using TMap.Server.Map;
using TMap.Server.Net;

// FullSaveCheck validates server prefix + disposable DB marker before calling either entry point.
internal static class WelcomeMailCheck
{
    public static async Task ChildAsync(string[] args, string connection)
    {
        var opt = new MapServerOptions { SaveQueueName = args[1] };
        opt.Db.GameConnectionString = connection;
        opt.Db.GlobalConnectionString = new SqlConnectionStringBuilder(connection) { InitialCatalog = "TGlobal_gsp" }.ConnectionString;
        var db = new GameDatabase(connection, opt.Db.GlobalConnectionString); var state = new MapState(); var t = new TemplateStore();
        foreach (ushort id in new ushort[] { 200, 201 }) t.Items[id] = new(id, 0, new float[4], Stack: 30);
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Debug));
        var service = new MapService(opt, state, new World(), db, t, logs.CreateLogger<MapService>())
            { WelcomeMailStore = db, OperatorPostStore = db };
        await service.VerifyWelcomeMailStorageAsync();
        if (args[0] == "--full-welcome-recover") { await service.RecoverWelcomeMailAsync(); return; }
        await service.InitItemIdSeedAsync(); // same prerequisite as MapWorker before the real CONREADY path
        service.OperatorPostEnqueue = _ => throw new IOException("Simulated exit before SQL transport acceptance");
        var ch = new Character { CharId = 9206, Name = "Welcome9206", DbLoaded = true, Hp = 100, MaxHp = 100 };
        var session = new ClientSession(new Client()) { CharId = 9206, UserId = 9206, Key = 1, Channel = 1,
            IsMain = true, State = EnterState.Granted, Char = ch };
        state.Register(session);
        await service.DispatchClientAsync(session, new PacketWriter(Msg.CS_CONREADY_REQ).ToArray());
        if (session.State != EnterState.InGame || (await db.PendingWelcomeMailAsync()).All(p => p.CharId != 9206))
            throw new InvalidOperationException($"First-entry dispatch did not freeze welcome mail in SQL: state={session.State}, main={session.IsMain}, loaded={ch.DbLoaded}, grid={session.Grid is not null}");
        Environment.Exit(85); // no logout/shutdown callback: pending mail must survive the process
    }

    public static async Task RunAsync(string connection, string scope, string migration, List<string> passed)
    {
        async Task Exec(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        async Task<long> Number(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
        void Require(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
        await SqlQueueFixture.ApplyMigration(connection, migration, sqlcmdDefaults: true);
        var db = new GameDatabase(connection);
        Require(!(await db.VerifyWelcomeMailStorageAsync()).Enabled && await Number("SELECT COUNT(*) FROM TCharacterWelcomeMail") == 0,
            "Welcome migration activated a reward or enrolled existing characters");
        async Task NewCharacter(int id)
        {
            await Exec($@"
DECLARE @columns nvarchar(max),@values nvarchar(max),@sql nvarchar(max);
SELECT @columns=STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(name)),',') WITHIN GROUP(ORDER BY column_id),
@values=STRING_AGG(CONVERT(nvarchar(max),CASE name WHEN 'dwCharID' THEN '{id}' WHEN 'dwUserID' THEN '{id}' WHEN 'szNAME' THEN '''Welcome{id}''' WHEN 'bDelete' THEN '0' ELSE QUOTENAME(name) END),',') WITHIN GROUP(ORDER BY column_id)
FROM sys.columns WHERE object_id=OBJECT_ID('dbo.TCHARTABLE') AND is_computed=0;
SET @sql=N'SET IDENTITY_INSERT TCHARTABLE ON; INSERT TCHARTABLE('+@columns+') SELECT '+@values+' FROM TCHARTABLE WHERE dwCharID=1; SET IDENTITY_INSERT TCHARTABLE OFF;'; EXEC(@sql);
");
        }
        OperatorPostIntent Build(WelcomeMailSettings settings, WelcomeMailIdentity identity) => new(identity.OperationId, identity.CharId,
            identity.Receiver, MapService.NetString(settings.Title), MapService.NetString(settings.Message), 1, 0, 0, 0, identity.Time,
            settings.Items.Select((item, slot) => new ItemSaveData(0, 2, 0, (byte)slot, item.ItemId, 0, (byte)item.Count, 0,
                70, 70, 0, 0, 0, new byte[6], new ushort[6], new uint[6], 0, 0)).ToList());
        await NewCharacter(9201);
        await Exec("INSERT TWelcomeMailItem VALUES(200,5),(201,5); UPDATE TWelcomeMailSettings SET Enabled=1,Title=N'Welcome',Message=N'Enjoy the game' WHERE SettingsId=1");
        Require(await db.PrepareWelcomeMailAsync(1, Build) is null && await db.PrepareWelcomeMailAsync(9201, Build) is null,
            "An old/disabled-era character was enrolled retroactively");
        passed.Add("welcome schema applies twice disabled, retains old characters and only registers births after SQL activation");
        for (int id = 9202; id <= 9206; id++) await NewCharacter(id);
        Require(await Number("SELECT COUNT(*) FROM TCharacterWelcomeMail") == 5, "New-character trigger did not register the created characters");
        int builds = 0;
        var prepared = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new GameDatabase(connection).PrepareWelcomeMailAsync(9202,
            (s, i) => { Interlocked.Increment(ref builds); return Build(s, i); })));
        var intent = prepared[0]!;
        Require(builds == 1 && prepared.All(p => p!.OperationId == intent.OperationId && JsonSerializer.Serialize(p) == JsonSerializer.Serialize(intent)),
            "Concurrent first entries produced different welcome mail identities/payloads");
        passed.Add("concurrent first-entry claims freeze one stable SQL operation and complete gift payload");
        await Exec("UPDATE TWelcomeMailSettings SET Title=N'Changed later'; UPDATE TWelcomeMailItem SET ItemCount=7 WHERE ItemId=200");
        var replay = await db.PrepareWelcomeMailAsync(9202, Build);
        Require(JsonSerializer.Serialize(replay) == JsonSerializer.Serialize(intent), "Configuration change mutated an accepted gift");
        await Exec("UPDATE TWelcomeMailSettings SET Title=N'Welcome'; UPDATE TWelcomeMailItem SET ItemCount=5 WHERE ItemId=200");
        passed.Add("accepted welcome text/counts/time survive configuration edits and fresh store instances");
        await Exec("ALTER TABLE TITEMTABLE ADD CONSTRAINT FixtureWelcomeFailure CHECK(dwOwnerID<>9202 OR bCount<5)");
        try
        {
            bool failed = false; try { await db.DeliverOperatorPostAsync(intent); } catch (SqlException) { failed = true; }
            Require(failed && await Number("SELECT COUNT(*) FROM TPOSTTABLE WHERE dwCharID=9202") == 0 &&
                await Number("SELECT COUNT(*) FROM TOperatorPostOperation WHERE CharId=9202") == 0, "Failed welcome attachment committed partial mail or receipt");
        }
        finally { await Exec("ALTER TABLE TITEMTABLE DROP CONSTRAINT FixtureWelcomeFailure"); }
        var delivered = await db.DeliverOperatorPostAsync(intent);
        Require(delivered.Created && (await db.PostItemsAsync(9202, delivered.PostId)).Count == 2, "Retried welcome gift lost an attachment");
        passed.Add("native welcome header/items/receipt roll back together on failure and retry with the original operation");
        await db.PostGetItemAsync(9202, delivered.PostId); Require(await db.PostDeleteAsync(9202, delivered.PostId) == 0, "Claimed welcome could not be deleted");
        var repeated = await db.DeliverOperatorPostAsync((await db.PrepareWelcomeMailAsync(9202, Build))!);
        Require(!repeated.Created && await Number("SELECT COUNT(*) FROM TPOSTTABLE WHERE dwCharID=9202") == 0, "Collected/deleted welcome was reissued");
        await db.MarkWelcomeMailDispatchedAsync(intent.OperationId);
        Require(await db.PrepareWelcomeMailAsync(9202, Build) is null, "Dispatched welcome was offered again");
        passed.Add("relog/replay after claim or deletion cannot recreate a welcome letter; permanent SQL identity remains");
        await Exec("UPDATE TWelcomeMailSettings SET Enabled=0");
        Require(await db.PrepareWelcomeMailAsync(9203, Build) is null, "Disabled welcome was prepared");
        await Exec("UPDATE TWelcomeMailSettings SET Enabled=1");
        Require(await db.PrepareWelcomeMailAsync(9203, Build) is null, "An entry while disabled became a later-login gift");
        passed.Add("disabling the feature at first entry does not defer a fresh gift to a later login");
        bool rejected = false;
        try { await db.PrepareWelcomeMailAsync(9204, (_, _) => throw new InvalidDataException("Invalid chart")); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && await Number("SELECT COUNT(*) FROM TCharacterWelcomeMail WHERE CharId=9204 AND FirstEnteredAt IS NULL AND Payload IS NULL") == 1,
            "Failed payload construction consumed first entry");
        var valid = (await db.PrepareWelcomeMailAsync(9204, Build))!;
        await db.DeliverOperatorPostAsync(valid); await db.MarkWelcomeMailDispatchedAsync(valid.OperationId);
        await Exec("UPDATE TCHARTABLE SET bDelete=1 WHERE dwCharID=9205");
        Require(await db.PrepareWelcomeMailAsync(9205, Build) is null, "Deleted character received a new gift");
        passed.Add("invalid chart preparation rolls eligibility back and deleted characters cannot receive a new welcome");
        async Task Run(string mode, int exit)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(mode); start.ArgumentList.Add(scope);
            using var p = Process.Start(start)!; var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50));
            Require(p.ExitCode == exit, "Welcome child failed: " + await stdout + await stderr);
        }
        await Run("--full-welcome-write", 85);
        Require((await db.PendingWelcomeMailAsync()).Any(p => p.CharId == 9206) && await Number("SELECT COUNT(*) FROM TPOSTTABLE WHERE dwCharID=9206") == 0,
            "Process exit lost the accepted first-entry welcome intent");
        await Run("--full-welcome-recover", 0); await Run("--full-welcome-recover", 0);
        Require(await Number("SELECT COUNT(*) FROM TPOSTTABLE WHERE dwCharID=9206") == 1 &&
            await Number("SELECT COUNT(*) FROM TITEMTABLE WHERE dwOwnerID=9206 AND bStorageType=2 AND bCount=5") == 2 &&
            await Number("SELECT COUNT(*) FROM TCharacterWelcomeMail WHERE CharId=9206 AND DispatchedAt IS NOT NULL") == 1,
            "Fresh-process welcome recovery lost/duplicated mail or gifts");
        passed.Add("real CONREADY freezes SQL welcome before a forced process exit; repeated fresh-process recovery delivers once with both gifts");
        await SqlQueueFixture.ApplyMigration(connection, migration, sqlcmdDefaults: true);
        Require((await db.VerifyWelcomeMailStorageAsync()).Enabled && await Number("SELECT COUNT(*) FROM TCharacterWelcomeMail") == 5,
            "Rerunning the migration reset SQL settings or permanent character identities");
        passed.Add("migration rerun preserves activated settings, gift items and handled/pending first-entry identities");
        await Exec("UPDATE TWelcomeMailSettings SET Enabled=0"); // later framework fixtures retain their original birth semantics
    }
    private sealed class Client : IClientChannel
    {
        public IPEndPoint? RemoteEndPoint => null; public void Send(byte[] p) { } public void Send(PacketWriter p) { } public void Close() { }
    }
    private sealed class World : IWorldSink { public void Send(byte[] p) { } public void Send(PacketWriter p) { } }
}
