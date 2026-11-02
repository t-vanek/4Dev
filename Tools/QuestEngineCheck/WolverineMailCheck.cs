using System.Collections.Concurrent;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using FourDev.Mail;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TMap.Data;
using Weasel.Core.Migrations;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine;

internal static class WolverineMailCheck
{
    private static void FastRecovery(WolverineOptions options)
    {
        options.Durability.HealthCheckPollingTime = TimeSpan.FromMilliseconds(250);
        options.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(2);
        options.Durability.FirstNodeReassignmentExecution = TimeSpan.FromMilliseconds(250);
        options.Durability.NodeReassignmentPollingTime = TimeSpan.FromMilliseconds(250);
    }
    private static OperatorPostIntent Intent(Guid? id = null) => new(id ?? Guid.NewGuid(), 1, "FullFixture", "00000009Wolverine", "", 1, 0, 500, 0,
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), [new(0, 2, 0, 0, 200, 7, 5, 4, 500, 450, 6, 0, 8, [1,2,3,4,5,6], [101,102,103,104,105,106], [11,12,13,14,15,16], 9, 201)]);
    public static async Task ChildAsync(string connection, string mode, Guid id)
    {
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        WolverineMailCrashAfterCommit.Target = id;
        builder.AddGameMail(connection, null, world: false, provision: false, listenForDelivery: mode == "--full-wolverine-crash", configure: opts =>
        {
            FastRecovery(opts);
            if (mode == "--full-wolverine-crash") opts.Policies.AddMiddleware<WolverineMailCrashAfterCommit>(c => c.MessageType == typeof(DeliverServerMail));
        });
        using var host = builder.Build(); await host.StartAsync();
        if (mode == "--full-wolverine-produce")
        {
            host.Services.GetRequiredService<WolverineMailStore>().Enqueue(Intent(id));
            var end = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < end)
            {
                await using var c = new SqlConnection(connection); await c.OpenAsync();
                await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM fourdev_mail_transport.wolverine_queue_mail_delivery WHERE CONVERT(varchar(max),body) LIKE '%{id:D}%'", c);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 0) Environment.Exit(83);
                await Task.Delay(50);
            }
            throw new TimeoutException("Wolverine producer did not durably reach SQL transport");
        }
        await Task.Delay(TimeSpan.FromSeconds(45)); throw new TimeoutException("Expected consumer post-commit crash did not happen");
    }
    private sealed class Drainer(MailNotificationInbox inbox, ConcurrentQueue<MailDelivered> seen) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { while (!stoppingToken.IsCancellationRequested) { inbox.Drain(seen.Enqueue); await Task.Delay(10, stoppingToken); } }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
    public static async Task RunAsync(string connection, string schemaPath, string migration)
    {
        var seen = new ConcurrentQueue<MailDelivered>(); var passed = new List<string>();
        void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        async Task<long> Number(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
        async Task Exec(string sql)
        { await using var c = new SqlConnection(connection); await c.OpenAsync(); await using var cmd = new SqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        async Task RunChild(string mode, Guid id, int expected)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add(mode); start.ArgumentList.Add(id.ToString("D"));
            using var p = Process.Start(start)!; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55));
            Require(p.ExitCode == expected, "Wolverine child failed: " + await error + await output);
        }
        async Task WaitFor(Func<Task<bool>> condition)
        {
            var end = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < end) { if (await condition()) return; await Task.Delay(50); }
            throw new TimeoutException("Wolverine mail did not complete within 30 seconds");
        }
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        builder.AddGameMail(connection, null, world: true, provision: true, configure: FastRecovery);
        builder.Services.AddSingleton(seen); builder.Services.AddHostedService<Drainer>();
        using var host = builder.Build(); await host.StartAsync();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        await File.WriteAllTextAsync(schemaPath, ((IDatabase)(IMessageDatabase)runtime.Storage).ToDatabaseScript());
        var mail = host.Services.GetRequiredService<WolverineMailStore>();
        await mail.VerifyOperatorPostSchemaAsync();
        passed.Add("shared Wolverine host starts with SQL persistence/transport and required native mail storage");
        var row = new ItemSaveData(0, 2, 0, 0, 200, 7, 5, 4, 500, 450, 6, 0, 8, [1,2,3,4,5,6], [101,102,103,104,105,106], [11,12,13,14,15,16], 9, 201);
        var intent = new OperatorPostIntent(Guid.NewGuid(), 1, "FullFixture", "00000009Wolverine", "", 1, 0, 500, 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), [row]);
        mail.Enqueue(intent);
        await WaitFor(async () => await Number($"SELECT COUNT(*) FROM TOperatorPostOperation WHERE OperationId='{intent.OperationId:D}'") == 1);
        uint expectedPostId = (uint)await Number($"SELECT PostId FROM TOperatorPostOperation WHERE OperationId='{intent.OperationId:D}'");
        await WaitFor(() => Task.FromResult(seen.Any(n => n.PostId == expectedPostId)));
        Require(await Number($"SELECT COUNT(*) FROM TPOSTTABLE WHERE dwPostID=(SELECT PostId FROM TOperatorPostOperation WHERE OperationId='{intent.OperationId:D}') AND dwSilver=500 AND bContain=1") == 1,
            "Wolverine lost native reward coins/attachment");
        passed.Add("durable SQL delivery creates native reward mail and transactionally publishes the actor notification");
        for (int i = 0; i < 3; i++) mail.Enqueue(intent);
        await Task.Delay(400);
        Require(await Number($"SELECT COUNT(*) FROM TOperatorPostOperation WHERE OperationId='{intent.OperationId:D}'") == 1 && seen.Count(n => n.PostId == expectedPostId) == 1,
            "Wolverine replay duplicated the receipt or notification");
        passed.Add("different Wolverine envelopes for the same permanent reward ID do not duplicate reward or notification");
        var counts = await mail.PostInfoAsync(1, 0); var page = await mail.PostListAsync(1, counts.BeginId);
        uint postId = (uint)await Number($"SELECT PostId FROM TOperatorPostOperation WHERE OperationId='{intent.OperationId:D}'");
        Require(page.Any(p => p.PostId == postId), "Wolverine mailbox query missed native mail");
        var view = await mail.PostViewAsync(1, postId); var items = await mail.PostItemsAsync(1, postId);
        Require(view.Result == 0 && view.Silver == 500 && items.Count == 1 && items[0].Gem == 9, "Wolverine view/items response failed");
        Require(await mail.PostDeleteAsync(1, postId) != 0, "Wolverine deleted an unclaimed reward");
        await Exec("CREATE TRIGGER dbo.FixtureClaimFailure ON dbo.TITEMTABLE INSTEAD OF DELETE AS BEGIN THROW 51046,'Injected mail item release failure',1; END");
        try
        {
            bool failed = false; try { await mail.PostGetItemAsync(1, postId); } catch (Exception) { failed = true; }
            Require(failed && (await mail.PostViewAsync(1, postId)).Silver == 500 && (await mail.PostItemsAsync(1, postId)).Count == 1,
                "Failed native claim lost mail coins or items before the actor could grant them");
        }
        finally { await Exec("DROP TRIGGER dbo.FixtureClaimFailure"); }
        passed.Add("failed native attachment release rolls mail coins/items back before the game actor can grant contents");
        await mail.PostGetItemAsync(1, postId); Require(await mail.PostDeleteAsync(1, postId) == 0, "Wolverine could not delete claimed mail");
        passed.Add("mail counts/list/view/attachments/claim/delete run through typed Wolverine handlers with native ownership rules");
        var multi = Intent() with { Gold = 0, Silver = 0, Cooper = 0, Items = [row, row with { ItemSlot = 1 }] };
        var multiMail = await mail.DeliverOperatorPostAsync(multi);
        Require(await mail.PostDeleteAsync(1, multiMail.PostId) == 2 && (await mail.PostItemsAsync(1, multiMail.PostId)).Count == 2,
            "Deleting a multi-attachment letter bypassed the unclaimed-item guard");
        passed.Add("native multi-attachment delete bug is guarded transactionally; two unclaimed items keep their mail header");

        var canSend = await mail.PostCanSendAsync("FullFixture", "FullFixture", 3);
        Require(canSend == (0, 1u) && (await mail.PostCanSendAsync("FullFixture", "MissingFixture", 3)).Result == 1,
            "Wolverine recipient lookup changed native result/receiver binding");
        passed.Add("recipient validation uses typed Wolverine results and preserves missing-recipient rejection");
        var bill = await mail.SavePostAsync(1, 2, "QuestFixture", "FullFixture", "Bill", "Fixture", 0, 2, 0, 0, 100, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Require(bill.Result == 0 && bill.RecvId == 2, "Wolverine bill header binding failed");
        long attachmentId = await Number("DECLARE @id bigint; EXEC dbo.TGenerateDBItemID @id OUTPUT; SELECT @id;");
        await mail.SavePostItemAsync(2, row with { DlId = attachmentId, StorageId = bill.PostId });
        Require((await mail.PendingBillsAsync(1)).Any(b => b.PostId == bill.PostId), "Wolverine pending bills lost the sender");
        var returned = await mail.PostBillsUpdateAsync(bill.PostId, "QuestFixture", 3);
        Require(returned.Result == 0 && returned.NewId == bill.PostId && (await mail.PostItemsAsync(1, bill.PostId)).Count == 1,
            "Wolverine bill return did not preserve the native item transfer");
        passed.Add("player header/attachment writes, pending bill lookup and native bill return are routed through Wolverine");

        await Exec("IF NOT EXISTS(SELECT 1 FROM TQTITLECHART WHERE dwQuestID=50000) INSERT TQTITLECHART(dwQuestID,szTitle,szMessage,szNPCName) VALUES(50000,'Wolverine quest','Body','NPC'),(50001,'Wolverine operation','Body','NPC')");
        Require((await mail.ReserveQuestPostAsync(50000, 1, [row])).Result == 0, "Wolverine legacy quest reservation failed");
        var quest = new QuestPostIntent(Guid.NewGuid(), 50001, 1, 0, 0, 1, 1, 0, 0, 0, new string('A', 64), [], [row]);
        Require((await mail.ReserveQuestPostAsync(quest)).Result == 0 && (await mail.ReserveQuestPostAsync(quest)).Result == 0,
            "Wolverine stable quest reservation failed");
        Require((await mail.LoadQuestPostOperationsAsync(1)).Any(p => p.Intent.OperationId == quest.OperationId), "Wolverine quest continuation was not readable");
        await mail.AcknowledgeQuestPostOperationsAsync(1, [quest.OperationId]);
        Require(!(await mail.LoadQuestPostOperationsAsync(1)).Any(p => p.Intent.OperationId == quest.OperationId), "Wolverine quest receipt was not applied");
        passed.Add("both quest reservation contracts, pending continuation reads and receipt acknowledgements use Wolverine without changing stable issuance IDs");
        var reserved = await mail.DeliverReservedPostAsync();
        Require(reserved.Result == 0 && reserved.PostId != 0, "Wolverine World reserved-mail delivery failed");
        passed.Add("World reserved quest mail is delivered through Wolverine with the native atomic header/attachment transaction");

        await Exec("CREATE TRIGGER fourdev_mail.FixtureWolverineOutboxFailure ON fourdev_mail.wolverine_outgoing_envelopes AFTER INSERT AS BEGIN THROW 51046,'Injected mail outbox failure',1; END");
        var rollback = Intent(); long beforeHeaders = await Number("SELECT COUNT(*) FROM TPOSTTABLE");
        try
        {
            bool failed = false; try { await mail.DeliverOperatorPostAsync(rollback); } catch (Exception) { failed = true; }
            Require(failed && await Number("SELECT COUNT(*) FROM TPOSTTABLE") == beforeHeaders &&
                await Number($"SELECT COUNT(*) FROM TOperatorPostOperation WHERE OperationId='{rollback.OperationId:D}'") == 0,
                "Wolverine outbox failure left a delivered reward without a notification intent");
        }
        finally { await Exec("DROP TRIGGER fourdev_mail.FixtureWolverineOutboxFailure"); }
        Require((await mail.DeliverOperatorPostAsync(rollback)).Created, "Rolled back Wolverine outbox operation cannot retry");
        passed.Add("Wolverine outgoing-envelope failure rolls native header/items/coins/receipt back in the same SQL transaction");

        // Import an old queue entry twice. The bridge must transfer, never run a second native issuer.
        var legacy = Intent();
        var opt = new TMap.Server.MapServerOptions { SaveQueueName = "wolverine-legacy-" + Guid.NewGuid().ToString("N") };
        opt.Db.GameConnectionString = connection;
        var service = new TMap.Server.Map.MapService(opt, new(), new DiscardWorld(), new GameDatabase(connection), new(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TMap.Server.Map.MapService>.Instance)
            { OperatorPostStore = mail, LegacyOperatorPostForward = mail.ForwardLegacyIntentAsync };
        using (var queue = (TMap.Server.Map.SqlSaveQueue)service.OpenSaveQueue()!)
        {
            queue.Append(TMap.Server.Map.SaveQueueEntry.ServerPosts([legacy])); await service.RecoverPendingSavesAsync();
            Require(queue.PendingCount == 0, "Legacy queue was not durably transferred to Wolverine");
            queue.Append(TMap.Server.Map.SaveQueueEntry.ServerPosts([legacy])); await service.RecoverPendingSavesAsync();
        }
        await WaitFor(async () => await Number($"SELECT COUNT(*) FROM TOperatorPostOperation WHERE OperationId='{legacy.OperationId:D}'") == 1);
        passed.Add("legacy SQL save-queue mail transfers its stable operation ID to Wolverine and cannot create a second issuer");
        await host.StopAsync();

        await SqlQueueFixture.ApplyMigration(connection, migration);
        passed.Add("versioned Wolverine SQL migration applies twice without purging queued or handled mail");
        var crashId = Guid.NewGuid();
        await RunChild("--full-wolverine-produce", crashId, 83);
        Require(await Number($"SELECT COUNT(*) FROM TOperatorPostOperation WHERE OperationId='{crashId:D}'") == 0,
            "Producer-only process consumed the queued reward");
        await RunChild("--full-wolverine-crash", crashId, 84);
        Require(await Number($"SELECT COUNT(*) FROM TOperatorPostOperation WHERE OperationId='{crashId:D}'") == 1,
            "Post-commit crash did not retain the permanent native reward receipt");
        var recoveredBuilder = Host.CreateApplicationBuilder(); recoveredBuilder.Logging.ClearProviders();
        recoveredBuilder.AddGameMail(connection, null, world: true, provision: false, configure: FastRecovery);
        recoveredBuilder.Services.AddSingleton(seen); recoveredBuilder.Services.AddHostedService<Drainer>();
        using var recovered = recoveredBuilder.Build(); await recovered.StartAsync();
        uint recoveredId = (uint)await Number($"SELECT PostId FROM TOperatorPostOperation WHERE OperationId='{crashId:D}'");
        await WaitFor(() => Task.FromResult(seen.Any(n => n.PostId == recoveredId)));
        await WaitFor(async () => await Number($"SELECT COUNT(*) FROM fourdev_mail.wolverine_incoming_envelopes WHERE status<>'Handled' AND CONVERT(varchar(max),body) LIKE '%{crashId:D}%'") == 0);
        Require(await Number($"SELECT COUNT(*) FROM TPOSTTABLE WHERE dwPostID={recoveredId}") == 1 && seen.Count(n => n.PostId == recoveredId) == 1,
            "Recovered Wolverine consumer duplicated mail or its notification");
        passed.Add("forced producer exit retains SQL transport work; forced consumer exit after native commit recovers inbox/outbox on a new host without another reward");
        await recovered.StopAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { passed, scope = "Wolverine SQL host integration; synthetic native mailbox and actor inbox; not a real client" }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class DiscardWorld : TMap.Server.Net.IWorldSink
    { public void Send(byte[] packet) { } public void Send(TMap.Protocol.PacketWriter packet) { } }
}

public sealed class WolverineMailCrashAfterCommit
{
    public static Guid Target;
    public static void After(DeliverServerMail message) { if (message.Intent.OperationId == Target) Environment.Exit(84); }
}
